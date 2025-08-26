// procwrapper.c
#define _GNU_SOURCE
#include <unistd.h>
#include <stdlib.h>
#include <stdio.h>
#include <fcntl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <string.h>
#include <pthread.h>
#include <errno.h>
#include <signal.h>

typedef struct {
    int   used;
    pid_t pid;
    int   stdout_fd;
    int   stderr_fd;
    int   exit_code; // -2 = running/not set, >=0 real exit code, -1 = error
} proc_entry;

#define MAX_PROCS 64
static proc_entry procs[MAX_PROCS];
static pthread_mutex_t procs_mutex = PTHREAD_MUTEX_INITIALIZER;

static int set_nonblocking(int fd) {
    int flags = fcntl(fd, F_GETFL, 0);
    if (flags == -1) return -1;
    return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

// Reap child if finished; ONLY set exit_code here.
// Do NOT close fds or clear 'used' so readers can drain and observe EOF.
static void reap_if_finished(int idx) {
    if (idx < 0 || idx >= MAX_PROCS) return;

    // Fast check: if we already have a final code, don't call waitpid again.
    pthread_mutex_lock(&procs_mutex);
    if (!procs[idx].used || procs[idx].exit_code != -2) {
        pthread_mutex_unlock(&procs_mutex);
        return;
    }
    pid_t pid = procs[idx].pid;
    pthread_mutex_unlock(&procs_mutex);

    int status = 0;
    pid_t r = waitpid(pid, &status, WNOHANG);

    pthread_mutex_lock(&procs_mutex);
    // Another thread might have set exit_code while we were in waitpid.
    if (!procs[idx].used || procs[idx].exit_code != -2) {
        pthread_mutex_unlock(&procs_mutex);
        return;
    }

    if (r == pid) {
        if (WIFEXITED(status))       procs[idx].exit_code = WEXITSTATUS(status);
        else if (WIFSIGNALED(status)) procs[idx].exit_code = 128 + WTERMSIG(status);
        else                          procs[idx].exit_code = -1;
    } else if (r == -1) {
        // Only mark -1 if we truly haven't recorded anything yet.
        procs[idx].exit_code = -1;
    }
    pthread_mutex_unlock(&procs_mutex);
}

// start_process: path is full path to binary, argv is NULL-terminated array of char* (C strings)
__attribute__((visibility("default")))
int start_process(const char* path, char* const argv[]) {
    if (!path || !argv) return -1;

    pthread_mutex_lock(&procs_mutex);
    int idx = -1;
    for (int i = 0; i < MAX_PROCS; ++i) {
        if (!procs[i].used) { idx = i; break; }
    }
    pthread_mutex_unlock(&procs_mutex);
    if (idx == -1) return -1;

    int outpipe[2];
    int errpipe[2];
    if (pipe(outpipe) == -1) return -1;
    if (pipe(errpipe) == -1) { close(outpipe[0]); close(outpipe[1]); return -1; }

    pid_t pid = fork();
    if (pid < 0) {
        close(outpipe[0]); close(outpipe[1]);
        close(errpipe[0]); close(errpipe[1]);
        return -1;
    }

    if (pid == 0) {
        // child
        signal(SIGINT,  SIG_DFL);
        signal(SIGTERM, SIG_DFL);

        close(outpipe[0]);
        close(errpipe[0]);
        dup2(outpipe[1], STDOUT_FILENO);
        dup2(errpipe[1], STDERR_FILENO);
        close(outpipe[1]);
        close(errpipe[1]);

        // execv - use provided argv
        execv(path, argv);
        // if execv fails: report reason then exit 127
        int e = errno;
        dprintf(STDERR_FILENO, "execv failed: %s (%d) path=%s\n", strerror(e), e, path);
        _exit(127);
    }

    // parent
    close(outpipe[1]);
    close(errpipe[1]);

    set_nonblocking(outpipe[0]);
    set_nonblocking(errpipe[0]);

    pthread_mutex_lock(&procs_mutex);
    procs[idx].used      = 1;
    procs[idx].pid       = pid;
    procs[idx].stdout_fd = outpipe[0];
    procs[idx].stderr_fd = errpipe[0];
    procs[idx].exit_code = -2; // running
    pthread_mutex_unlock(&procs_mutex);

    return idx;
}

static void maybe_clear_slot_after_eof(int handle) {
    // Called with mutex locked by caller
    if (handle < 0 || handle >= MAX_PROCS) return;
    if (!procs[handle].used) return;
    if (procs[handle].stdout_fd < 0 && procs[handle].stderr_fd < 0 && procs[handle].exit_code >= 0) {
        procs[handle].used = 0;
    }
}

__attribute__((visibility("default")))
int read_stdout(int handle, char* buffer, int buflen) {
    if (!buffer || buflen <= 0) return -1;
    if (handle < 0 || handle >= MAX_PROCS) return -1;

    pthread_mutex_lock(&procs_mutex);
    int fd = procs[handle].stdout_fd;
    pthread_mutex_unlock(&procs_mutex);
    if (fd < 0) return 0;

    ssize_t n = read(fd, buffer, buflen);
    if (n == 0) {
        // EOF: close and possibly free slot
        pthread_mutex_lock(&procs_mutex);
        if (procs[handle].stdout_fd >= 0) {
            close(procs[handle].stdout_fd);
            procs[handle].stdout_fd = -1;
            maybe_clear_slot_after_eof(handle);
        }
        pthread_mutex_unlock(&procs_mutex);
        return 0;
    }
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) return 0;
        return -1;
    }
    return (int)n;
}

__attribute__((visibility("default")))
int read_stderr(int handle, char* buffer, int buflen) {
    if (!buffer || buflen <= 0) return -1;
    if (handle < 0 || handle >= MAX_PROCS) return -1;

    pthread_mutex_lock(&procs_mutex);
    int fd = procs[handle].stderr_fd;
    pthread_mutex_unlock(&procs_mutex);
    if (fd < 0) return 0;

    ssize_t n = read(fd, buffer, buflen);
    if (n == 0) {
        // EOF: close and possibly free slot
        pthread_mutex_lock(&procs_mutex);
        if (procs[handle].stderr_fd >= 0) {
            close(procs[handle].stderr_fd);
            procs[handle].stderr_fd = -1;
            maybe_clear_slot_after_eof(handle);
        }
        pthread_mutex_unlock(&procs_mutex);
        return 0;
    }
    if (n < 0) {
        if (errno == EAGAIN || errno == EWOULDBLOCK) return 0;
        return -1;
    }
    return (int)n;
}

// is_running: returns 1 if running, 0 if not running (exited or invalid)
__attribute__((visibility("default")))
int is_running(int handle) {
    if (handle < 0 || handle >= MAX_PROCS) return 0;
    reap_if_finished(handle);
    pthread_mutex_lock(&procs_mutex);
    int in_use = procs[handle].used && procs[handle].exit_code == -2;
    pthread_mutex_unlock(&procs_mutex);
    return in_use ? 1 : 0;
}

// get_exit_code: >=0 exit code, -2 still running, -1 error/invalid handle
__attribute__((visibility("default")))
int get_exit_code(int handle) {
    if (handle < 0 || handle >= MAX_PROCS) return -1;
    reap_if_finished(handle);
    pthread_mutex_lock(&procs_mutex);
    int ec = procs[handle].exit_code;
    pthread_mutex_unlock(&procs_mutex);
    return ec;
}

// stop_process: try SIGTERM then SIGKILL; returns 0 on success, -1 on error
__attribute__((visibility("default")))
int stop_process(int handle) {
    if (handle < 0 || handle >= MAX_PROCS) return -1;

    pthread_mutex_lock(&procs_mutex);
    if (!procs[handle].used && procs[handle].exit_code != -2) {
        pthread_mutex_unlock(&procs_mutex);
        return 0; // already not running
    }
    pid_t pid = procs[handle].pid;
    pthread_mutex_unlock(&procs_mutex);

    if (kill(pid, SIGTERM) == -1) {
        if (errno == ESRCH) return 0; // no such process
    }

    // small wait for graceful shutdown
    for (int i = 0; i < 10; ++i) {
        int status = 0;
        pid_t r = waitpid(pid, &status, WNOHANG);
        if (r == pid) break;
        usleep(100 * 1000);
    }
    // if still alive, SIGKILL
    if (kill(pid, 0) == 0) {
        kill(pid, SIGKILL);
        waitpid(pid, NULL, 0);
    }

    // mark exit code if we can reap here too
    reap_if_finished(handle);
    return 0;
}

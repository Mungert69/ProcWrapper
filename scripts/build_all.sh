#!/usr/bin/env bash
set -euo pipefail

### CONFIG ##############################################################
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC_DIR="${ROOT_DIR}/native"
OUT_DIR="${ROOT_DIR}/artifacts"
MAUI_LIBS_ROOT="${ROOT_DIR}/scripts/maui-output/Platforms/Android/NativeLibraries"
RID_OUT_ROOT="${ROOT_DIR}/runtimes"   # where .NET will auto-load from

ANDROID_ABIS=("arm64-v8a" "armeabi-v7a")
API_LEVEL="${ANDROID_API_LEVEL:-21}"

# Try to locate NDK if present (for Android builds)
detect_ndk() {
  local ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
  if [[ -n "${ndk:-}" && -d "$ndk" ]]; then echo "$ndk"; return 0; fi
  for base in "${ANDROID_SDK_ROOT:-}" "${ANDROID_HOME:-}"; do
    if [[ -n "$base" && -d "$base/ndk" ]]; then
      local cand
      cand=$(ls -d "$base"/ndk/* 2>/dev/null | sort -V | tail -n1 || true)
      if [[ -n "$cand" && -d "$cand" ]]; then echo "$cand"; return 0; fi
    fi
  done
  if [[ -d "/usr/lib/android-sdk/ndk" ]]; then
    local cand
    cand=$(ls -d /usr/lib/android-sdk/ndk/* 2>/dev/null | sort -V | tail -n1 || true)
    if [[ -n "$cand" && -d "$cand" ]]; then echo "$cand"; return 0; fi
  fi
  return 1
}

### CLEAN ###############################################################
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR" "$MAUI_LIBS_ROOT" "$RID_OUT_ROOT"

echo "🌿 Source: $SRC_DIR"
echo "📦 Artifacts: $OUT_DIR"
echo

### HOST BUILD: linux-x64 ###############################################
echo "🛠  Building host linux-x64..."
HOST64_BUILD="$OUT_DIR/build-host-x64"
HOST64_INSTALL="$OUT_DIR/install-host-x64"
rm -rf "$HOST64_BUILD" "$HOST64_INSTALL"
mkdir -p "$HOST64_BUILD" "$HOST64_INSTALL"

cmake -S "$SRC_DIR" -B "$HOST64_BUILD" \
  -D CMAKE_BUILD_TYPE=Release \
  -D CMAKE_LIBRARY_OUTPUT_DIRECTORY="$HOST64_INSTALL"

cmake --build "$HOST64_BUILD" --config Release -- -j"$(nproc)"

HOST64_SO="$(find "$HOST64_INSTALL" "$HOST64_BUILD" -maxdepth 3 -name 'libprocwrapper.so' -print -quit)"
if [[ -z "$HOST64_SO" ]]; then
  echo "❌ linux-x64 lib not found"
  exit 1
fi

RID64_DIR="$RID_OUT_ROOT/linux-x64/native"
mkdir -p "$RID64_DIR"
cp -v "$HOST64_SO" "$RID64_DIR/libprocwrapper.so"
echo "✅ linux-x64 → $RID64_DIR/libprocwrapper.so"
echo

### HOST BUILD: linux-x86 (optional) ####################################
echo "🛠  Attempting host linux-x86 (requires 32-bit toolchain & multilib)..."
if command -v gcc >/dev/null 2>&1 && gcc -m32 -v >/dev/null 2>&1; then
  HOST86_BUILD="$OUT_DIR/build-host-x86"
  HOST86_INSTALL="$OUT_DIR/install-host-x86"
  rm -rf "$HOST86_BUILD" "$HOST86_INSTALL"
  mkdir -p "$HOST86_BUILD" "$HOST86_INSTALL"

  cmake -S "$SRC_DIR" -B "$HOST86_BUILD" \
    -D CMAKE_BUILD_TYPE=Release \
    -D CMAKE_C_FLAGS="-m32" \
    -D CMAKE_EXE_LINKER_FLAGS="-m32" \
    -D CMAKE_SHARED_LINKER_FLAGS="-m32" \
    -D CMAKE_LIBRARY_OUTPUT_DIRECTORY="$HOST86_INSTALL"

  cmake --build "$HOST86_BUILD" --config Release -- -j"$(nproc)" || {
    echo "⚠️  linux-x86 build failed (likely missing 32-bit dev libs). Skipping."
  }

  HOST86_SO="$(find "$HOST86_INSTALL" "$HOST86_BUILD" -maxdepth 3 -name 'libprocwrapper.so' -print -quit || true)"
  if [[ -n "$HOST86_SO" ]]; then
    RID86_DIR="$RID_OUT_ROOT/linux-x86/native"
    mkdir -p "$RID86_DIR"
    cp -v "$HOST86_SO" "$RID86_DIR/libprocwrapper.so"
    echo "✅ linux-x86 → $RID86_DIR/libprocwrapper.so"
  else
    echo "ℹ️  linux-x86 artifact not produced."
  fi
else
  echo "ℹ️  gcc -m32 not available; skipping linux-x86. (Install gcc-multilib & 32-bit libc dev to enable.)"
fi
echo

### ANDROID BUILDS ######################################################
if NDK="$(detect_ndk)"; then
  echo "📱 Using Android NDK: $NDK"
  for ABI in "${ANDROID_ABIS[@]}"; do
    echo "🛠  Building Android ${ABI}..."
    A_BUILD="$OUT_DIR/build-android-$ABI"
    A_INSTALL="$OUT_DIR/install-android-$ABI"
    rm -rf "$A_BUILD" "$A_INSTALL"
    mkdir -p "$A_BUILD" "$A_INSTALL"

    cmake -S "$SRC_DIR" -B "$A_BUILD" \
      -D CMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
      -D ANDROID_ABI="$ABI" \
      -D ANDROID_PLATFORM="android-${API_LEVEL}" \
      -D ANDROID_STL="c++_static" \
      -D CMAKE_BUILD_TYPE=Release \
      -D CMAKE_LIBRARY_OUTPUT_DIRECTORY="$A_INSTALL"

    cmake --build "$A_BUILD" --config Release -- -j"$(nproc)"

    A_SO="$(find "$A_INSTALL" "$A_BUILD" -maxdepth 3 -name 'libprocwrapper.so' -print -quit || true)"
    if [[ -z "$A_SO" ]]; then
      echo "❌ libprocwrapper.so not found for $ABI"
      exit 1
    fi

    DEST_DIR="$MAUI_LIBS_ROOT/$ABI"
    mkdir -p "$DEST_DIR"
    cp -v "$A_SO" "$DEST_DIR/libprocwrapper.so"
    echo "✅ $ABI → $DEST_DIR/libprocwrapper.so"
    echo
  done
else
  echo "ℹ️  Android NDK not found — skipping Android ABIs. Set ANDROID_NDK_HOME to enable."
fi

echo "🎉 Done."
echo "   Host libs placed under: $RID_OUT_ROOT/{linux-x64,linux-x86}/native/libprocwrapper.so"
echo "   Android libs placed under: $MAUI_LIBS_ROOT/{arm64-v8a,armeabi-v7a}/libprocwrapper.so"


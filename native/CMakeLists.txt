cmake_minimum_required(VERSION 3.18)
project(procwrapper C)

add_library(procwrapper SHARED
    procwrapper.c
)

set_property(TARGET procwrapper PROPERTY POSITION_INDEPENDENT_CODE ON)

# Link pthread where needed (Linux host). On Android, bionic provides it implicitly.
if(ANDROID)
    # nothing special needed; keep it lean
else()
    find_package(Threads REQUIRED)
    target_link_libraries(procwrapper PRIVATE Threads::Threads)
endif()


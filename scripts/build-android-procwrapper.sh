#!/usr/bin/env bash
set -euo pipefail

### Robust NDK detection ################################################

pick_latest_ndk_dir() {
  local base="$1"
  [[ -d "$base/ndk" ]] || return 1
  # pick highest semantic version folder
  local latest
  latest=$(ls -d "$base/ndk"/* 2>/dev/null | sort -V | tail -n1 || true)
  [[ -n "$latest" && -d "$latest" ]] || return 1
  echo "$latest"
}

NDK="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"

if [[ -z "${NDK:-}" || ! -d "${NDK}" ]]; then
  # try common SDK envs
  for base in "${ANDROID_SDK_ROOT:-}" "${ANDROID_HOME:-}"; do
    if [[ -n "$base" && -d "$base" ]]; then
      cand=$(pick_latest_ndk_dir "$base" || true)
      if [[ -n "$cand" ]]; then NDK="$cand"; break; fi
    fi
  done
fi

if [[ -z "${NDK:-}" || ! -d "${NDK}" ]]; then
  # Debian/Ubuntu packaged location fallback
  if [[ -d "/usr/lib/android-sdk/ndk" ]]; then
    cand=$(ls -d /usr/lib/android-sdk/ndk/* 2>/dev/null | sort -V | tail -n1 || true)
    if [[ -n "$cand" && -d "$cand" ]]; then NDK="$cand"; fi
  fi
fi

if [[ -z "${NDK:-}" || ! -d "${NDK}" ]]; then
  echo "❌ Could not locate Android NDK."
  echo "   Try: export ANDROID_NDK_HOME=/usr/lib/android-sdk/ndk/28.0.13004108"
  exit 1
fi

echo "✅ Using NDK: $NDK"

API_LEVEL="${ANDROID_API_LEVEL:-21}"
ABIS=("arm64-v8a" "armeabi-v7a")

# Source dir (where CMakeLists.txt lives)
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../native" && pwd)"
BUILD_ROOT="$(pwd)/build-android-procwrapper"
MAUI_PROJ_ROOT="${MAUI_PROJ_ROOT:-$(pwd)/maui-output}"
MAUI_LIBS_ROOT="${MAUI_PROJ_ROOT}/Platforms/Android/NativeLibraries"


#######################################################################

echo "✅ Using NDK: ${NDK}"
echo "✅ API level: ${API_LEVEL}"
echo "✅ Source dir: ${SRC_DIR}"
echo "✅ Build root: ${BUILD_ROOT}"
echo "✅ MAUI NativeLibraries root: ${MAUI_LIBS_ROOT}"
echo

mkdir -p "${BUILD_ROOT}"

for ABI in "${ABIS[@]}"; do
  echo "🛠  Building for ${ABI}..."

  BUILD_DIR="${BUILD_ROOT}/build-${ABI}"
  INSTALL_DIR="${BUILD_ROOT}/install-${ABI}"
  rm -rf "${BUILD_DIR}" "${INSTALL_DIR}"
  mkdir -p "${BUILD_DIR}" "${INSTALL_DIR}"

  cmake -S "${SRC_DIR}" -B "${BUILD_DIR}" \
    -D CMAKE_TOOLCHAIN_FILE="${NDK}/build/cmake/android.toolchain.cmake" \
    -D ANDROID_ABI="${ABI}" \
    -D ANDROID_PLATFORM="android-${API_LEVEL}" \
    -D ANDROID_STL="c++_static" \
    -D CMAKE_BUILD_TYPE=Release \
    -D CMAKE_LIBRARY_OUTPUT_DIRECTORY="${INSTALL_DIR}"

  cmake --build "${BUILD_DIR}" --config Release -- -j"$(nproc)"

  # The built .so should be under ${INSTALL_DIR} or ${BUILD_DIR}
  SO_PATH="$(find "${INSTALL_DIR}" "${BUILD_DIR}" -maxdepth 3 -name 'libprocwrapper.so' -print -quit || true)"
  if [[ -z "${SO_PATH}" ]]; then
    echo "❌ libprocwrapper.so not found for ${ABI}"
    exit 1
  fi

  # Copy into MAUI project structure
  DEST_DIR="${MAUI_LIBS_ROOT}/${ABI}"
  mkdir -p "${DEST_DIR}"
  cp -v "${SO_PATH}" "${DEST_DIR}/libprocwrapper.so"

  echo "✅ ${ABI} → ${DEST_DIR}/libprocwrapper.so"
  echo
done

echo "🎉 Done."
echo "   Drop-in location (verify or adjust in your MAUI project):"
echo "   ${MAUI_LIBS_ROOT}/{arm64-v8a,armeabi-v7a}/libprocwrapper.so"


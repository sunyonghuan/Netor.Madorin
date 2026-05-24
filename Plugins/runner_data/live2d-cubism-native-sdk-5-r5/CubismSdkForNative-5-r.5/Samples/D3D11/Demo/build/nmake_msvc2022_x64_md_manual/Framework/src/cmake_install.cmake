# Install script for directory: E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Framework/src

# Set the install prefix
if(NOT DEFINED CMAKE_INSTALL_PREFIX)
  set(CMAKE_INSTALL_PREFIX "C:/Program Files (x86)/Demo")
endif()
string(REGEX REPLACE "/$" "" CMAKE_INSTALL_PREFIX "${CMAKE_INSTALL_PREFIX}")

# Set the install configuration name.
if(NOT DEFINED CMAKE_INSTALL_CONFIG_NAME)
  if(BUILD_TYPE)
    string(REGEX REPLACE "^[^A-Za-z0-9_]+" ""
           CMAKE_INSTALL_CONFIG_NAME "${BUILD_TYPE}")
  else()
    set(CMAKE_INSTALL_CONFIG_NAME "Release")
  endif()
  message(STATUS "Install configuration: \"${CMAKE_INSTALL_CONFIG_NAME}\"")
endif()

# Set the component getting installed.
if(NOT CMAKE_INSTALL_COMPONENT)
  if(COMPONENT)
    message(STATUS "Install component: \"${COMPONENT}\"")
    set(CMAKE_INSTALL_COMPONENT "${COMPONENT}")
  else()
    set(CMAKE_INSTALL_COMPONENT)
  endif()
endif()

# Is this installation the result of a crosscompile?
if(NOT DEFINED CMAKE_CROSSCOMPILING)
  set(CMAKE_CROSSCOMPILING "FALSE")
endif()

if(NOT CMAKE_INSTALL_LOCAL_ONLY)
  # Include the install script for each subdirectory.
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Effect/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Id/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Math/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Model/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Motion/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Physics/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Rendering/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Type/cmake_install.cmake")
  include("E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/Utils/cmake_install.cmake")

endif()

string(REPLACE ";" "\n" CMAKE_INSTALL_MANIFEST_CONTENT
       "${CMAKE_INSTALL_MANIFEST_FILES}")
if(CMAKE_INSTALL_LOCAL_ONLY)
  file(WRITE "E:/Netor.me/Cortana/Plugins/runner_data/live2d-cubism-native-sdk-5-r5/CubismSdkForNative-5-r.5/Samples/D3D11/Demo/build/nmake_msvc2022_x64_md_manual/Framework/src/install_local_manifest.txt"
     "${CMAKE_INSTALL_MANIFEST_CONTENT}")
endif()

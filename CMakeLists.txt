cmake_minimum_required(VERSION 3.24)
# Windows-GNU enables RC while configuring CXX, so locate windres before project().
get_filename_component(_compiler_name "${CMAKE_CXX_COMPILER}" NAME)
if(_compiler_name STREQUAL "g++.exe")
  get_filename_component(_compiler_dir "${CMAKE_CXX_COMPILER}" DIRECTORY)
  set(CMAKE_RC_COMPILER "${_compiler_dir}/windres.exe" CACHE FILEPATH "MinGW Windows resource compiler" FORCE)
endif()
project(JPVision VERSION 0.7.2 LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(MINGW)
  set(_runtime_default OFF)
  set(_studio_default ON)
else()
  set(_runtime_default ON)
  set(_studio_default OFF)
endif()
option(JPV_BUILD_RUNTIME "Build MSVC OpenCV runtime" ${_runtime_default})
option(JPV_BUILD_STUDIO "Build Qt MinGW editor" ${_studio_default})
set(JPV_OPENCV_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." CACHE PATH "Existing OpenCV distribution")
set(JPV_STUDIO_OUTPUT_ROOT "${CMAKE_SOURCE_DIR}/artifacts/studio" CACHE PATH "Studio binary output root")
if(JPV_BUILD_STUDIO)
  enable_language(RC)
  set(JPV_ICON_FILE "${CMAKE_CURRENT_SOURCE_DIR}/assets/jpVision.ico")
  configure_file(native/studio/jpvision.rc.in "${CMAKE_CURRENT_BINARY_DIR}/jpvision.rc" @ONLY)
  set_source_files_properties("${CMAKE_CURRENT_BINARY_DIR}/jpvision.rc" PROPERTIES OBJECT_DEPENDS "${JPV_ICON_FILE}")
  find_package(Qt6 6.12.0 EXACT REQUIRED COMPONENTS Widgets Concurrent Test)
  set(BUILD_TESTING OFF CACHE BOOL "QtNodes tests" FORCE)
  set(BUILD_EXAMPLES OFF CACHE BOOL "QtNodes examples" FORCE)
  set(BUILD_DOCS OFF CACHE BOOL "QtNodes docs" FORCE)
  set(BUILD_SHARED_LIBS OFF CACHE BOOL "Static QtNodes; Qt itself remains dynamic" FORCE)
  set(QT_NODES_DEVELOPER_DEFAULTS OFF CACHE BOOL "QtNodes developer options" FORCE)
  add_subdirectory(third_party/nodeeditor EXCLUDE_FROM_ALL)
  add_executable(JPVision.Studio WIN32 native/studio/main.cpp native/studio/StudioWindow.cpp native/studio/StudioWindow.h native/studio/StudioSmoke.cpp
    native/studio/StudioUi.cpp native/studio/StudioUiSmoke.cpp native/studio/StudioTheme.cpp native/studio/StudioTheme.h
    native/studio/StudioAlgorithms.cpp native/studio/MeasurementUi.cpp native/studio/MeasurementUi.h native/studio/RegionEditor.cpp native/studio/RegionEditor.h
    native/studio/StudioTemplateSmoke.cpp
    native/studio/ImageRegions.cpp native/studio/RegionItem.cpp native/studio/RegionItem.h
    native/studio/RegionGeometry.cpp native/studio/RegionGeometry.h
    native/studio/VisionNodePainter.cpp native/studio/VisionNodePainter.h
    native/studio/HistogramWidget.cpp native/studio/HistogramWidget.h native/studio/RunLog.cpp native/studio/RunLog.h
    native/studio/GraphModel.cpp native/studio/GraphModel.h native/studio/GraphView.h native/studio/ImageView.cpp native/studio/ImageView.h
    native/studio/InterfaceDialog.cpp native/studio/InterfaceDialog.h native/studio/NativeApi.h
    native/studio/jpvision.qrc "${CMAKE_CURRENT_BINARY_DIR}/jpvision.rc")
  set_target_properties(JPVision.Studio PROPERTIES AUTOMOC ON AUTORCC ON OUTPUT_NAME "JP-Vision"
    RUNTIME_OUTPUT_DIRECTORY "${JPV_STUDIO_OUTPUT_ROOT}/${CMAKE_BUILD_TYPE}")
  target_include_directories(JPVision.Studio PRIVATE native/capi)
  target_compile_definitions(JPVision.Studio PRIVATE JPV_VERSION="${PROJECT_VERSION}")
  target_link_libraries(JPVision.Studio PRIVATE QtNodes::QtNodes Qt6::Widgets Qt6::Concurrent Qt6::Test)
  add_executable(JPVision.GraphTests native/tests/graph_tests.cpp native/studio/GraphModel.cpp native/studio/GraphModel.h
    native/studio/InterfaceDialog.cpp native/studio/InterfaceDialog.h
    native/studio/ImageView.cpp native/studio/ImageView.h native/studio/ImageRegions.cpp
    native/studio/RegionItem.cpp native/studio/RegionItem.h native/studio/RegionGeometry.cpp native/studio/RegionGeometry.h)
  target_include_directories(JPVision.GraphTests PRIVATE native/studio native/capi)
  target_link_libraries(JPVision.GraphTests PRIVATE QtNodes::QtNodes Qt6::Widgets Qt6::Test)
  set_target_properties(JPVision.GraphTests PROPERTIES AUTOMOC ON RUNTIME_OUTPUT_DIRECTORY "${JPV_STUDIO_OUTPUT_ROOT}/${CMAKE_BUILD_TYPE}")
  add_custom_command(TARGET JPVision.Studio POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${CMAKE_SOURCE_DIR}/artifacts/native/${CMAKE_BUILD_TYPE}/JPVision.Runtime.dll" "$<TARGET_FILE_DIR:JPVision.Studio>"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "$<IF:$<CONFIG:Debug>,${JPV_OPENCV_ROOT}/bin/opencv_world500d.dll,${JPV_OPENCV_ROOT}/bin/opencv_world500.dll>" "$<TARGET_FILE_DIR:JPVision.Studio>"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_SOURCE_DIR}/samples/images" "$<TARGET_FILE_DIR:JPVision.Studio>/samples/images"
    COMMAND ${CMAKE_COMMAND} -E copy_directory "${CMAKE_SOURCE_DIR}/samples/recipes" "$<TARGET_FILE_DIR:JPVision.Studio>/samples/recipes")
endif()
if(MSVC)
  add_compile_options(/utf-8 /W4 /permissive-)
  add_compile_definitions(NOMINMAX WIN32_LEAN_AND_MEAN _CRT_SECURE_NO_WARNINGS)
endif()
if(JPV_BUILD_RUNTIME)
  if(NOT MSVC)
    message(FATAL_ERROR "The installed OpenCV import library requires MSVC. Studio uses a separate MinGW build.")
  endif()
  add_library(OpenCVWorld SHARED IMPORTED)
  set_target_properties(OpenCVWorld PROPERTIES
    IMPORTED_IMPLIB_RELEASE "${JPV_OPENCV_ROOT}/lib/opencv_world500.lib"
    IMPORTED_LOCATION_RELEASE "${JPV_OPENCV_ROOT}/bin/opencv_world500.dll"
    IMPORTED_IMPLIB_DEBUG "${JPV_OPENCV_ROOT}/lib/opencv_world500d.lib"
    IMPORTED_LOCATION_DEBUG "${JPV_OPENCV_ROOT}/bin/opencv_world500d.dll"
    MAP_IMPORTED_CONFIG_RELWITHDEBINFO Release
    MAP_IMPORTED_CONFIG_MINSIZEREL Release
    INTERFACE_INCLUDE_DIRECTORIES "${JPV_OPENCV_ROOT}/include")
  add_executable(JPVision.Probe native/tests/probe.cpp)
  target_link_libraries(JPVision.Probe PRIVATE OpenCVWorld)
  set_target_properties(JPVision.Probe PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>")
  add_custom_command(TARGET JPVision.Probe POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "$<TARGET_FILE:OpenCVWorld>" "$<TARGET_FILE_DIR:JPVision.Probe>")
  enable_testing()
  add_test(NAME opencv_probe COMMAND JPVision.Probe)
  if(NOT EXISTS "${CMAKE_SOURCE_DIR}/config/trusted_key.h")
    message(FATAL_ERROR "Configure the owner's public trust anchor with scripts/bootstrap.ps1 first.")
  endif()
  add_library(JPVision.Runtime SHARED native/capi/runtime.cpp native/licensing/license.cpp native/core/graph.cpp native/core/roi.cpp
    native/core/components/component.cpp native/core/components/basic_components.cpp
    native/core/components/geometry_components.cpp native/core/components/custom_components.cpp
    native/core/components/caliper_components.cpp native/core/components/template_components.cpp
    native/core/components/measurement_components.cpp native/core/fixture.cpp
    native/core/image_pipeline.cpp native/core/components/image_components.cpp
    native/core/components/image_measurement.cpp native/core/components/annular_components.cpp
    native/core/components/edge_point_components.cpp
    native/core/components/blob_components.cpp
    native/core/deep_learning.cpp native/core/components/deep_learning_components.cpp
    native/core/parameter_bindings.cpp native/core/parameter_expression.cpp)
  target_compile_definitions(JPVision.Runtime PRIVATE JPV_VERSION="${PROJECT_VERSION}")
  target_include_directories(JPVision.Runtime PRIVATE third_party native/core native/licensing config PUBLIC native/capi)
  target_link_libraries(JPVision.Runtime PRIVATE OpenCVWorld bcrypt crypt32 wbemuuid ole32 oleaut32 shell32 advapi32)
  set_target_properties(JPVision.Runtime PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>")
  add_custom_command(TARGET JPVision.Runtime POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "$<TARGET_FILE:OpenCVWorld>" "$<TARGET_FILE_DIR:JPVision.Runtime>")
  set(_jpv_ort_directory "${CMAKE_SOURCE_DIR}/artifacts/dependencies/onnxruntime/win-x64")
  foreach(_jpv_ort_library onnxruntime.dll onnxruntime_providers_shared.dll)
    if(EXISTS "${_jpv_ort_directory}/${_jpv_ort_library}")
      add_custom_command(TARGET JPVision.Runtime POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${_jpv_ort_directory}/${_jpv_ort_library}" "$<TARGET_FILE_DIR:JPVision.Runtime>")
    endif()
  endforeach()
  add_executable(JPVision.RuntimeCli native/tests/runtime_cli.cpp)
  target_include_directories(JPVision.RuntimeCli PRIVATE third_party)
  target_link_libraries(JPVision.RuntimeCli PRIVATE JPVision.Runtime OpenCVWorld)
  set_target_properties(JPVision.RuntimeCli PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>")
  add_executable(JPVision.NativeTests native/tests/native_tests.cpp)
  target_include_directories(JPVision.NativeTests PRIVATE third_party)
  target_link_libraries(JPVision.NativeTests PRIVATE JPVision.Runtime)
  set_target_properties(JPVision.NativeTests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>")
  add_test(NAME native_contract COMMAND JPVision.NativeTests "${CMAKE_SOURCE_DIR}/samples/recipes/parts-count.vrecipe"
    "${CMAKE_SOURCE_DIR}/artifacts/activation/current.vlic" "${CMAKE_SOURCE_DIR}/artifacts/tests/native-store-$<CONFIG>")
  add_executable(JPVision.ExpressionTests native/tests/parameter_expression_tests.cpp native/core/parameter_expression.cpp)
  target_include_directories(JPVision.ExpressionTests PRIVATE third_party native/core native/capi)
  set_target_properties(JPVision.ExpressionTests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>")
  add_test(NAME parameter_expressions COMMAND JPVision.ExpressionTests)
  set(JPV_MVS_ROOT "C:/Program Files (x86)/MVS/Development" CACHE PATH "Optional installed Hikrobot MVS development directory")
  find_path(JPV_MVS_INCLUDE_DIR MvCameraControl.h HINTS "${JPV_MVS_ROOT}/Includes")
  if(JPV_MVS_INCLUDE_DIR AND CMAKE_SIZEOF_VOID_P EQUAL 8)
    add_library(JPVision.Camera.Mvs SHARED native/camera/mvs_camera.cpp native/camera/mvs_camera.h)
    target_include_directories(JPVision.Camera.Mvs PRIVATE third_party "${JPV_MVS_INCLUDE_DIR}")
    target_link_libraries(JPVision.Camera.Mvs PRIVATE shell32 ole32 uuid)
    set_target_properties(JPVision.Camera.Mvs PROPERTIES
      RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>"
      ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/artifacts/native/$<CONFIG>")
  else()
    message(STATUS "Optional MVS camera bridge disabled: installed x64 MVS headers not found")
  endif()
endif()


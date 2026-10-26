add_library(libScePad SHARED EXCLUDE_FROM_ALL
    ${CMAKE_CURRENT_SOURCE_DIR}/Export.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/PadState.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/WheelBridge.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/WheelInput.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/src/WideCamera.cpp
    ${CMAKE_CURRENT_SOURCE_DIR}/../libSceVideoOut/src/InputMapping.cpp
)
target_include_directories(libScePad PRIVATE ${LIBS_INCLUDE_DIR} ${CMAKE_SOURCE_DIR}/3rdparty/SDL2/include)
target_link_libraries(libScePad PRIVATE SDL2-static)
if(MINGW)
    # Keep the bundled SDL out of the export table.
    target_link_options(libScePad PRIVATE -Wl,--exclude-libs,ALL)
endif()
set_target_properties(libScePad PROPERTIES CXX_EXTENSIONS OFF)
configure_windows_unwind(libScePad)

if(MINGW)
    foreach(runtimeLinkFlag IN ITEMS -static-libgcc -static-libstdc++ -static)
        string(REPLACE "${runtimeLinkFlag}" "" CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS}")
    endforeach()
endif()

if(BUILD_TESTING)
    add_test_executable(guest_pad_tests ${LIBS_INCLUDE_DIR}/tests/GuestPad.cpp)
    target_include_directories(guest_pad_tests PRIVATE ${LIBS_INCLUDE_DIR})
    target_link_libraries(guest_pad_tests PRIVATE libScePad libc)
    configure_windows_unwind(guest_pad_tests)
    add_test(NAME guest_pad COMMAND guest_pad_tests)
    if(WIN32)
        set_tests_properties(guest_pad PROPERTIES ENVIRONMENT_MODIFICATION
            "PATH=path_list_prepend:$<TARGET_FILE_DIR:libScePad>")
    endif()
endif()

# The launcher's wheel mapping window reads live wheel state through this helper, so it sees the
# same devices and numbers as libScePad.
add_executable(ryty-wheel-probe ${CMAKE_CURRENT_SOURCE_DIR}/tools/WheelProbe.cpp)
target_include_directories(ryty-wheel-probe PRIVATE ${CMAKE_SOURCE_DIR}/3rdparty/SDL2/include)
target_link_libraries(ryty-wheel-probe PRIVATE SDL2-static)
if(MINGW)
    target_link_options(ryty-wheel-probe PRIVATE -static -static-libgcc -static-libstdc++)
endif()

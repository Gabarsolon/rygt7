function(ryty_patch_sdl_directinput)
    find_package(Git REQUIRED)
    get_target_property(sdl_dir SDL2-static SOURCE_DIR)
    set(windows_dir "${sdl_dir}/src/haptic/windows")
    set(original "${windows_dir}/SDL_dinputhaptic.c")
    set(generated_dir "${CMAKE_CURRENT_BINARY_DIR}/sdl2-directinput")
    set(patched "${generated_dir}/SDL_dinputhaptic.c")
    set(patch "${CMAKE_CURRENT_FUNCTION_LIST_DIR}/sdl2-directinput.patch")

    get_target_property(sources SDL2-static SOURCES)
    if(NOT original IN_LIST sources)
        message(FATAL_ERROR "Cannot find the pinned SDL DirectInput backend in SDL2-static")
    endif()
    file(MAKE_DIRECTORY "${generated_dir}")
    add_custom_command(OUTPUT "${patched}"
        COMMAND "${CMAKE_COMMAND}" -E copy "${original}" "${patched}"
        COMMAND "${CMAKE_COMMAND}" -E env "GIT_CEILING_DIRECTORIES=${CMAKE_CURRENT_BINARY_DIR}" "${GIT_EXECUTABLE}" apply --no-index -p4 "${patch}"
        WORKING_DIRECTORY "${generated_dir}"
        DEPENDS "${original}" "${patch}"
        VERBATIM)
    add_custom_target(ryty_sdl2_directinput DEPENDS "${patched}")
    add_dependencies(SDL2-static ryty_sdl2_directinput)
    list(REMOVE_ITEM sources "${original}")
    list(APPEND sources "${patched}")
    set_property(TARGET SDL2-static PROPERTY SOURCES "${sources}")
    set_property(SOURCE "${patched}" TARGET_DIRECTORY SDL2-static PROPERTY INCLUDE_DIRECTORIES "${windows_dir}")
endfunction()

ryty_patch_sdl_directinput()

# FEAT-GTS-253: placeholder libraries for PS4 libraries GT7 links against but ryGT7 does not
# implement. The converted GT7 executable loads libs/<library>.prx for every one of them at start, so each
# must exist. They are built from the export lists here (see README.md) straight into the patched
# library folder: their exports are already NIDs, so they need no NID patching.
if(NOT WIN32)
    return()
endif()
enable_language(C)
find_package(Python3 REQUIRED COMPONENTS Interpreter)

file(GLOB RYTY_PLACEHOLDER_LISTS CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/*.nids)
set(RYTY_PLACEHOLDER_TARGETS)
foreach(listing ${RYTY_PLACEHOLDER_LISTS})
    get_filename_component(library ${listing} NAME_WE)
    set(source ${CMAKE_CURRENT_BINARY_DIR}/${library}.c)
    set(definition ${CMAKE_CURRENT_BINARY_DIR}/${library}.def)
    add_custom_command(
        OUTPUT ${source} ${definition}
        COMMAND ${Python3_EXECUTABLE} ${CMAKE_CURRENT_SOURCE_DIR}/generate.py ${listing} ${source} ${definition}
        DEPENDS ${listing} ${CMAKE_CURRENT_SOURCE_DIR}/generate.py
        VERBATIM)
    string(MAKE_C_IDENTIFIER "placeholder_${library}" target)
    add_library(${target} SHARED ${source} ${definition})
    set_target_properties(${target} PROPERTIES
        OUTPUT_NAME ${library} PREFIX "" SUFFIX ".prx"
        RUNTIME_OUTPUT_DIRECTORY ${RYTY_PLACEHOLDER_OUTPUT}
        LIBRARY_OUTPUT_DIRECTORY ${RYTY_PLACEHOLDER_OUTPUT}
        ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_CURRENT_BINARY_DIR}
        INTERPROCEDURAL_OPTIMIZATION FALSE)
    target_compile_options(${target} PRIVATE -O2)
    target_link_options(${target} PRIVATE -s)
    list(APPEND RYTY_PLACEHOLDER_TARGETS ${target})
endforeach()
set(RYTY_PLACEHOLDER_TARGETS ${RYTY_PLACEHOLDER_TARGETS} PARENT_SCOPE)

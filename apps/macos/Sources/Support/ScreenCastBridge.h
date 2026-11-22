//
//  ScreenCastBridge.h
//  Bridging header for the Rust network core.
//
//  `screencast_core.h` is the authoritative C ABI (14 functions). It is resolved through
//  HEADER_SEARCH_PATHS = $(SRCROOT)/../../native/include, so the same header that
//  `native/tools/header_smoke.c` compiles is what Swift sees here.
//

#ifndef SCREENCAST_MACOS_BRIDGE_H
#define SCREENCAST_MACOS_BRIDGE_H

#include "screencast_core.h"

#endif /* SCREENCAST_MACOS_BRIDGE_H */

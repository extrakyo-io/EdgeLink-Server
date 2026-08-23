#pragma once

// Umbrella header — pulls in every EdgeLink class.
//
// This file must live in src/ : a library that ships library.properties uses the
// Arduino 1.5 layout, where only src/ is added to the include path. While this
// header sat in the library root, `#include <EdgeLink.h>` simply did not resolve
// (the bundled examples worked because they include the per-class headers directly).
#include "EdgeLinkTCP.h"
#include "EdgeLinkUDP.h"
#include "EdgeLinkAsyncUDP.h"   // Only active when <AsyncUDP.h> / <ESPAsyncUDP.h> is available

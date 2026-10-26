# KEF LSX II volume proxy: HAL driver + LSX II Volume Fixer menu bar agent
BUILD := build
DRIVER := $(BUILD)/KEFProxy.driver
APP := $(BUILD)/LSXIIVolumeFixer.app
HELPER := $(APP)/Contents/MacOS/LSXIIVolumeFixer
HELPER_SRC := $(wildcard Helper/*.swift)
# Oldest supported macOS; must match LSMinimumSystemVersion in Helper/Info.plist.
MACOS_MIN := 14.0
# Native architecture of the Mac, also from a Rosetta shell (where `uname -m` says x86_64): an x86_64
# driver would not load into an arm64 coreaudiod.
ARCH := $(shell [ "$$(sysctl -n hw.optional.arm64 2>/dev/null)" = 1 ] && echo arm64 || echo x86_64)
CXXFLAGS := -std=c++17 -O2 -fblocks -Wall -Wextra -Wno-unused-parameter -arch $(ARCH) -mmacosx-version-min=$(MACOS_MIN)
SWIFTFLAGS := -O -target $(ARCH)-apple-macos$(MACOS_MIN)
FRAMEWORKS := -framework CoreAudio -framework CoreFoundation

all: driver helper

driver: $(DRIVER)/Contents/MacOS/KEFProxy

$(DRIVER)/Contents/MacOS/KEFProxy: Driver/KEFProxyDriver.cpp Driver/Info.plist
	mkdir -p $(DRIVER)/Contents/MacOS $(DRIVER)/Contents/Resources
	clang++ $(CXXFLAGS) -bundle -o $@ Driver/KEFProxyDriver.cpp $(FRAMEWORKS)
	cp Driver/Info.plist $(DRIVER)/Contents/Info.plist
	codesign --force --sign - $(DRIVER)

helper: $(HELPER)

$(HELPER): $(HELPER_SRC) Helper/Info.plist
	mkdir -p $(APP)/Contents/MacOS $(APP)/Contents/Resources
	swiftc $(SWIFTFLAGS) -o $@ $(HELPER_SRC)
	cp Helper/Info.plist $(APP)/Contents/Info.plist
	codesign --force --sign - $(APP)

TEST_BIN := $(BUILD)/config-tests

# Pure-function tests for the volume mapping (Helper/Config.swift): key-step table, overshoot, curve.
test: $(TEST_BIN) test-install test-uninstall
	$(TEST_BIN)

# install.sh must pass --pref values through the admin script byte for byte, including `"`, `\` and `'`.
# --dry-run builds, prints the admin script and installs nothing.
test-install:
	Tests/install_dry_run.sh

# uninstall.sh --dry-run prints the admin script and removes nothing.
test-uninstall:
	Tests/uninstall_dry_run.sh

$(TEST_BIN): Helper/Config.swift Helper/SpeakerDiscovery.swift Tests/ConfigTests.swift
	mkdir -p $(BUILD)
	swiftc $(SWIFTFLAGS) -module-name ConfigTests -o $@ Helper/Config.swift Helper/SpeakerDiscovery.swift Tests/ConfigTests.swift

clean:
	rm -rf $(BUILD)

.PHONY: all driver helper test test-install test-uninstall clean

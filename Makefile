APP     := askui-debug
OUT     := bin
PUBLISH := dotnet publish -c Release -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true

.PHONY: all build-windows build-darwin build-linux clean run

all: build-windows build-darwin build-linux

# ── Windows ───────────────────────────────────────────────────────────────────

build-windows:
	$(PUBLISH) -r win-x64   -o $(OUT)/win-x64
	$(PUBLISH) -r win-arm64 -o $(OUT)/win-arm64
	mv $(OUT)/win-x64/$(APP).exe   $(OUT)/$(APP)-windows-x64.exe   2>/dev/null || true
	mv $(OUT)/win-arm64/$(APP).exe $(OUT)/$(APP)-windows-arm64.exe 2>/dev/null || true

# ── macOS ─────────────────────────────────────────────────────────────────────

build-darwin:
	$(PUBLISH) -r osx-arm64 -o $(OUT)/osx-arm64
	$(PUBLISH) -r osx-x64   -o $(OUT)/osx-x64
	mv $(OUT)/osx-arm64/$(APP) $(OUT)/$(APP)-darwin-arm64 2>/dev/null || true
	mv $(OUT)/osx-x64/$(APP)   $(OUT)/$(APP)-darwin-x64   2>/dev/null || true
	chmod +x $(OUT)/$(APP)-darwin-* 2>/dev/null || true

# ── Linux ─────────────────────────────────────────────────────────────────────

build-linux:
	$(PUBLISH) -r linux-x64   -o $(OUT)/linux-x64
	$(PUBLISH) -r linux-arm64 -o $(OUT)/linux-arm64
	mv $(OUT)/linux-x64/$(APP)   $(OUT)/$(APP)-linux-x64   2>/dev/null || true
	mv $(OUT)/linux-arm64/$(APP) $(OUT)/$(APP)-linux-arm64 2>/dev/null || true
	chmod +x $(OUT)/$(APP)-linux-* 2>/dev/null || true

# ── Dev ───────────────────────────────────────────────────────────────────────

run:
	dotnet run -- $(ARGS)

clean:
	rm -rf $(OUT)/ bin/ obj/

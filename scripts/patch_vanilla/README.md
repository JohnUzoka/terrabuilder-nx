# patch_vanilla

Reproducible Cecil patcher for a clean GOG Terraria 1.4.5.8 install. It emits the managed assemblies used by the Switch build: `Terraria.exe`, extracted/patched `ReLogic.dll`, patched `FNA.dll`, `NxCrypto.dll`, `NxInputDiag.dll`, and `patch_vanilla_receipt.json`.

```bash
python3 scripts/patch_vanilla/run.py "$HOME/GOG Games/Terraria1_4_5_8/game" \
  --out "$HOME/.cache/terraria-switch-build/patch-vanilla-work/verified-output"
```

The driver builds inside `localhost/monobuild:local` and writes only below `~/.cache/terraria-switch-build/patch-vanilla-work/`.

Patches: LinuxLaunch caches embedded ReLogic resolution; player save/load redirects AES-CBC transforms to `NxCrypto`; background force-load, Chroma, Windows performance diagnostics, and WeGame dispatcher are disabled for Switch; TimeLogger uses ordered per-instance storage; draw/hint prepasses avoid unnecessary work; TCP connection probing avoids disposed/lazy streams; AOT-inlining flags protect display methods. FNA roots content at `romfs:/`, installs Switch input diagnostics, latches gamepad/tick phases, unbinds render targets on dispose, marks hot FNA3D pinvokes with `SuppressGCTransition`, and shares one FAudio engine.

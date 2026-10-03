# patch_vanilla

Reproducible Cecil patcher for a clean GOG Terraria 1.4.5.8 install. It emits the managed assemblies used by the Switch build: `Terraria.exe`, extracted/patched `ReLogic.dll`, patched `FNA.dll`, `NxCrypto.dll`, `NxInputDiag.dll`, the other dependency DLLs embedded in `Terraria.exe` (unpatched), and `patch_vanilla_receipt.json`.

```bash
TB=~/.cache/terrabuilder
python3 scripts/patch_vanilla/run.py "$HOME/GOG Games/Terraria1_4_5_8/game" \
  --workdir $TB --toolchain $TB/toolchain --out $TB/scratch/patch-vanilla
```

The driver builds inside `localhost/monobuild:local` without network access, using the toolchain's .NET SDK and Mono.Cecil. It writes only below `<workdir>/patch-vanilla-work/` and `--out`, which must be under `--workdir`. The CLI's patch stage runs it with `--out <workdir>/patch/<inputs hash>`.

Patches: LinuxLaunch caches embedded ReLogic resolution; player save/load redirects AES-CBC transforms to `NxCrypto`; background force-load, Chroma, Windows performance diagnostics, and WeGame dispatcher are disabled for Switch; TimeLogger uses ordered per-instance storage; draw/hint prepasses avoid unnecessary work; TCP connection probing avoids disposed/lazy streams; AOT-inlining flags protect display methods. FNA roots content at `romfs:/`, installs Switch input diagnostics, latches gamepad/tick phases, unbinds render targets on dispose, marks hot FNA3D pinvokes with `SuppressGCTransition`, and shares one FAudio engine.

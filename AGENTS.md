# Optional guidance for coding assistants

- Read `README.md` for setup and `Tests/README.md` for validation.
- Prefer focused changes through the existing native hooks and shader assets.
- Preserve render-thread ownership, GL state restoration, and allocation-free frame paths.
- Verify engine APIs and shader contracts against Vintage Story 1.22.7 and SheyderMod 1.1.3; do not guess them.
- Keep machine paths, game binaries, generated files, and agent notes out of source control.
- Build and run relevant checks; report limitations in runtime or visual validation.

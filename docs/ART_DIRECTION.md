# Living Studio art direction and asset provenance

The implemented direction is a cozy isometric night studio: original anime chibi adults, dark navy/lavender surfaces, walnut furniture, amber task lights and jade/blue monitors. Readable HTML panels retain the cream/jade application theme. Day lighting is an alternate scene palette. This is an updated design direction implemented in runtime assets, not a claim of pixel-identical reproduction of the older work-package concept.

## Production assets

| File under `src/AICompanyOS.Runtime/wwwroot/` | Contents | Runtime use |
|---|---|---|
| `assets/office/team-atlas.png` | Transparent 4×2 atlas, 1774×887, eight original role-themed full-body chibi characters | Office actors, roster, agent cards, selected-agent inspector, starter preview and live board portraits |
| `assets/office/decor-atlas.png` | Transparent 4×2 atlas, 1774×887, workstation, meeting table, bookshelf, plant, sofa, planning board, server cabinet and lamp | Independently placed scene props with depth ordering |

Both atlases were created for this project using the built-in image generation tool. Runtime crops use SVG viewports over equal atlas cells; the generated PNGs retain their alpha and are copied into the repository without image editing. The complete source images are included in the app package, approximately 3.5 MiB combined. The layer, camera, collision and animation code lives in `office.js`, `office-assets.js` and `studio.css`.

These are generated original designs, with prompts excluding franchise characters, logos and watermarks. No third-party artist attribution or exclusive ownership is asserted. Keep this provenance record when redistributing the files and review distribution requirements for your product separately.

## Composition and behavior

1. The SVG floor and wall layers establish the existing 18×12 navigation grid.
2. Furniture and character atlas cells share the same isometric scene coordinates. Desk/meeting tiles stay blocked; added decorative props sit outside navigable perimeter tiles.
3. Draw ordering uses grid depth; characters move with the existing reservation system instead of crossing occupied cells.
4. Avatar body transforms supply procedural 2D breathing/walking/working motion; public states supply icons and links. There are no secret-thinking bubbles or model calls to animate a frame.
5. Selection and hover reveal the full name/status, while the roster and inspector provide readable text. Names, messages and controls are not baked into the images.
6. Reduced motion, visual pause, hidden views and board-only mode retain the same execution state. Lighting/camera/filter changes are presentation preferences.

## Generation prompts

The exact prompts used to create the two atlases are retained in [assets-prompts.json](assets-prompts.json). Both requests specified a transparent background. Tool: built-in `image_gen.imagegen`; no API-key CLI fallback was used.

# Cracked Ice

`CrackedIce.mat` is ready to place on any mesh. Surface, normal, cloud, and crack
patterns use world-space triplanar projection, so mesh UVs and Transform scale
do not change their size. Pattern-size controls are measured in metres.

The ready-made material uses the original article's ice diffuse, normal,
roughness, and RGB-packed crack textures from
`Assets/Snowfield/Textures/CrackedIce`. Attribution and the upstream license are
included in that folder.

**Surface De-Tiling** blends a rigidly rotated second projection in broad
world-space patches. Increase it to hide repetition; **Variation Patch Size
(m)** controls how large those patches are. The diffuse, normal, and roughness
maps share the same variation, so their details remain aligned.

Specular lighting uses the dielectric workflow with an ice IOR of 1.31 rather
than a metallic approximation. The roughness texture is interpreted as
roughness (not smoothness), and **Specular Anti-Aliasing** broadens highlights
only where sub-pixel normal variation would otherwise sparkle.

**Base Smoothness** describes clear ice. Frost coverage blends toward **Frost
Roughness**, while visible cracks blend toward **Cracked Area Roughness**. This
keeps an energy-conserving reflection but spreads its highlight across damaged
or cloudy areas instead of leaving the whole sheet mirror-like.

The depth illusion is easiest to read on a broad, mostly flat surface while the
camera moves at a shallow angle. Start by adjusting **Crack Depth (m)** and
**Crack Pattern Size (m)**. **Grazing Angle Clamp** limits displacement near the
horizon while preserving the separation between the three subsurface planes.

For authored cracks, pack three grayscale crack masks into the R, G, and B
channels of one texture, assign it to **Packed Cracks (RGB)**, and enable **Use
Packed Crack Texture**. Disable sRGB on a packed mask texture. R is the nearest
sheet, G is the middle sheet, and B is the deepest sheet.

The shader is an opaque URP material. It creates apparent internal depth and
keeps normal depth writing, shadows, fog, lightmaps, and additional lights. It
does not provide physically transparent ice or scene-color refraction.

Technique inspired by Daniel Pokladek's “Cracked Ice Shader (using Parallax
effect)”: https://danielpokladek.wordpress.com/2021/01/04/cracked-ice-shader-using-parallax-effect/

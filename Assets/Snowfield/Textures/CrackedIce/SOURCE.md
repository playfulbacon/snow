# Cracked Ice texture source

These textures are copied without pixel edits from Daniel Pokladek's cracked-ice
Unity project:

- Repository: https://github.com/danielpokladek-shaders/cracked-ice
- Article: https://danielpokladek.wordpress.com/2021/01/04/cracked-ice-shader-using-parallax-effect/
- Upstream commit: `795a798257347c3d5de5d2ebd279d91d71c2aca7`

The upstream repository identifies the project as MIT licensed unless otherwise
specified and acknowledges Binary Impact as the original tutorial texture source.
The copied MIT license is included beside this file and is reproduced
byte-for-byte from the upstream repository. A project-level attribution entry is
also maintained in `/THIRD_PARTY_NOTICES.md`.

`ci_cracks.png` stores three independent grayscale crack masks in RGB. Its Unity
import is intentionally linear (`sRGBTexture: 0`), as is `ci_roughness.png`.

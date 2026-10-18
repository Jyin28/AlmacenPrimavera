# Local OCR components

These files are unmodified distributions copied by `scripts/vendor-ocr.mjs`.
Versions are pinned in `package.json` and `pnpm-lock.yaml`; `manifest.json` records
the size and SHA-256 of each copied asset.

- Tesseract.js 7.0.0: https://github.com/naptha/tesseract.js — Apache-2.0;
  see `LICENSE-tesseract-js.txt` and the bundled `*.js.LICENSE.txt` notices.
- Tesseract.js-core (version resolved in pnpm-lock.yaml):
  https://github.com/naptha/tesseract.js-core — see `LICENSE-tesseract-core.txt`.
- English and Spanish `4.0.0_best_int` traineddata from
  `@tesseract.js-data/eng` and `@tesseract.js-data/spa` 1.0.0:
  https://github.com/naptha/tessdata. Upstream traineddata repository license:
  https://github.com/naptha/tessdata/blob/gh-pages/LICENSE (Apache-2.0; the license
  text is also included in `LICENSE-tesseract-js.txt`). The npm wrappers declare
  MIT; their original metadata and READMEs are preserved alongside the assets.

Runtime requests use local application paths only. Images are not sent to these
upstream projects or to a hosted OCR service.

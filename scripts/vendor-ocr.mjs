// Reproducible static-asset build. Runtime OCR never contacts a CDN.
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdir, copyFile, readdir, writeFile, readFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';

const require = createRequire(import.meta.url);
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const destination = join(root, 'wwwroot/vendor/tesseract');
await mkdir(join(destination, 'core'), { recursive: true });
await mkdir(join(destination, 'lang'), { recursive: true });
const engine = dirname(require.resolve('tesseract.js/package.json'));
const core = dirname(createRequire(join(engine, 'package.json')).resolve('tesseract.js-core/package.json'));
const files = [];
async function copy(source, relative) {
    const target = join(destination, relative);
    await copyFile(source, target);
    const bytes = await readFile(target);
    files.push({ file: relative.replaceAll('\\', '/'), bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') });
}
await copy(join(engine, 'dist/tesseract.min.js'), 'tesseract.min.js');
await copy(join(engine, 'dist/worker.min.js'), 'worker.min.js');
for (const name of ['tesseract.min.js.LICENSE.txt', 'worker.min.js.LICENSE.txt']) await copy(join(engine, 'dist', name), name);
await copy(join(engine, 'LICENSE.md'), 'LICENSE-tesseract-js.txt');
await copy(join(core, 'LICENSE'), 'LICENSE-tesseract-core.txt');
for (const name of await readdir(core)) {
    if (/^tesseract-core.*\.wasm(?:\.js)?$/.test(name)) await copy(join(core, name), join('core', name));
}
for (const language of ['eng', 'spa']) {
    const folder = dirname(require.resolve('@tesseract.js-data/' + language + '/package.json'));
    await copy(join(folder, '4.0.0_best_int', language + '.traineddata.gz'), join('lang', language + '.traineddata.gz'));
    await copy(join(folder, 'README.md'), 'README-' + language + '.md');
    await copy(join(folder, 'package.json'), 'package-' + language + '.json');
}
await writeFile(join(destination, 'manifest.json'), JSON.stringify({ engine: 'tesseract.js', version: '7.0.0', files }, null, 2) + '\n');
console.log('Local OCR assets ready:', files.length, 'files;', Math.round(files.reduce((n, f) => n + f.bytes, 0) / 1048576), 'MB.');

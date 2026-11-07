// Offline unit check of the self-contained report, with a minimal DOM model.
// Does not launch a browser, transmit files, or validate actual CSS rendering.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const {fileURLToPath, pathToFileURL} = require('node:url');
const html = fs.readFileSync(process.argv[2], 'utf8');
const payload = html.match(/<script id="data" type="application\/json">([\s\S]*?)<\/script>/)[1];
const data = JSON.parse(payload);

function boot(page, overridePayload) {
const embedded = page.match(/<script id="data" type="application\/json">([\s\S]*?)<\/script>/)[1];
const script = page.match(/<script>\s*([\s\S]*?)<\/script>/)[1];
const ids = new Map();
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.listeners = {}; this.attrs = {}; this._value = ''; this.textContent = ''; this.hidden = false; }
  get value() { return this.tag === 'select' ? (this.children.find(c => c.value === this._value) || this.children[0])?.value || '' : this._value; }
  set value(v) { this._value = String(v); }
  append(child) { this.children.push(child); }
  replaceChildren(...children) { this.children = children; }
  setAttribute(name, value) { this.attrs[name] = String(value); if (name === 'id') ids.set(String(value), this); }
  addEventListener(name, fn) { this.listeners[name] = fn; }
}
for (const match of page.matchAll(/<(\w+)[^>]*\bid="([^"]+)"[^>]*>/g)) {
  const el = new Element(match[1]);
  el.hidden = /\shidden(?:\s|>)/.test(match[0]);
  el.value = match[0].match(/\bvalue="([^"]*)"/)?.[1] || '';
  ids.set(match[2], el);
}
for (const select of page.matchAll(/<select\b[^>]*\bid="([^"]+)"[^>]*>([\s\S]*?)<\/select>/g))
  for (const match of select[2].matchAll(/<option\b[^>]*\bvalue="([^"]+)"[^>]*>([\s\S]*?)<\/option>/g)) {
    const option = new Element('option'); option.value = match[1]; option.textContent = match[2]; ids.get(select[1]).append(option);
  }
ids.get('data').textContent = overridePayload === undefined ? embedded : overridePayload;
const document = { getElementById: id => { assert(ids.has(id), 'missing element ' + id); return ids.get(id); },
  createElement: tag => new Element(tag), createElementNS: (_, tag) => new Element(tag) };
vm.runInNewContext(script, { document, console }, { timeout: 5000 });
return ids;
}

const template = fs.readFileSync(path.join(__dirname, 'viewer.template.html'), 'utf8');
const bare = boot(template);
assert.equal(bare.get('workspace').hidden, true);
assert.equal(bare.get('loadStatus').hidden, false);
assert.match(bare.get('loadStatus').textContent, /模板.*report\.html/);
for (const broken of ['{"cases":', '{"cases":[],"summaries":[]}', '{"cases":[{}],"summaries":[{}]}']) {
  const failed = boot(template, broken);
  assert.equal(failed.get('workspace').hidden, true);
  assert.equal(failed.get('loadStatus').hidden, false);
  assert.match(failed.get('loadStatus').textContent, /无法读取|初始化失败/);
}
const ids = boot(html);
assert.equal(ids.get('workspace').hidden, false);
assert.equal(ids.get('loadStatus').hidden, true);
assert.equal(ids.get('path').children.length, data.cases.filter(c => c.Model === ids.get('model').value).length);
let cases = 0;
for (const model of data.summaries.map(s => s.model)) {
  ids.get('model').value = model;
  const rows = data.cases.filter(r => r.Model === model);
  for (let i = 0; i < rows.length; i++) {
    ids.get('path').value = String(i); ids.get('path').listeners.change();
    const row = rows[i];
    assert.equal(ids.get('workspace').hidden, false, ids.get('loadStatus').textContent);
    assert(ids.get('caseTitle').textContent.includes(row.Code));
    assert.equal(ids.get('steps').children.length, row.Steps.length + 1);
    assert.equal(ids.get('native').children.length, (row.Native?.length || 0) + 1);
    for (const position of [0, 200, 400]) {
      ids.get('progress').value = String(position); ids.get('progress').listeners.input();
      assert.equal(ids.get('workspace').hidden, false, ids.get('loadStatus').textContent);
      assert(Math.abs(Number(ids.get('cursor').attrs.cx) - row.Path[position][0]) < 1e-10);
      assert(Math.abs(Number(ids.get('cursor').attrs.cy) + row.Path[position][1]) < 1e-10);
      assert(!ids.get('position').textContent.includes('NaN'));
    }
    cases++;
  }
}
for (const launcher of [path.join(__dirname,'viewer.html'), path.join(__dirname,'bin/Release/net10.0/viewer.html')]) {
  const entry = fs.readFileSync(launcher,'utf8');
  assert(!entry.includes('__REPORT_DATA__'));
  const href = entry.match(/id="reportLink" href="([^"]+)"/)[1].replace(/&amp;/g,'&');
  const refresh = entry.match(/http-equiv="refresh" content="0; url=([^"]+)"/)[1].replace(/&amp;/g,'&');
  assert.equal(href,refresh);
  const target = fileURLToPath(new URL(href,pathToFileURL(launcher)));
  assert(fs.existsSync(target),'launcher target missing: '+target);
  const linked = boot(fs.readFileSync(target,'utf8'));
  assert.equal(linked.get('workspace').hidden,false,'launcher points to an unusable page');
}
console.log(`PASS report script: ${cases} cases, ${data.summaries.length} region models, tables and 3 cursor positions; raw template and corrupt data show errors; 2 launcher targets load; no browser rendering asserted`);

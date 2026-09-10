import { readFile } from "node:fs/promises";

const catalogPath = process.argv[2] ?? "FaloopCatalog.cs";
const huntCatalogPath = process.argv[3] ?? "HuntCatalog.cs";
const clientPath = process.argv[4] ?? "FaloopClient.cs";
const catalog = await readFile(catalogPath, "utf8");
const huntCatalog = await readFile(huntCatalogPath, "utf8");
const client = await readFile(clientPath, "utf8");

const home = await fetch("https://faloop.app/");
if (!home.ok) throw new Error(`Faloop page failed: ${home.status}`);
const html = await home.text();
const mainPath = [...html.matchAll(/<script[^>]+src="([^"]*main\.[^"]+\.js)"/g)].at(-1)?.[1];
if (!mainPath) throw new Error("Could not identify Faloop's current main client bundle.");
const bundleUrl = new URL(mainPath, home.url).href;
const bundleResponse = await fetch(bundleUrl);
if (!bundleResponse.ok) throw new Error(`Faloop bundle failed: ${bundleResponse.status}`);
const source = await bundleResponse.text();

function extractObject(text, brace) {
  let depth = 0;
  let quoted = false;
  let escaped = false;
  for (let i = brace; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (escaped) escaped = false;
      else if (ch === "\\") escaped = true;
      else if (ch === '"') quoted = false;
      continue;
    }
    if (ch === '"') quoted = true;
    else if (ch === "{") depth++;
    else if (ch === "}" && --depth === 0) return text.slice(brace, i + 1);
  }
  throw new Error(`Unterminated object at ${brace}`);
}

function extractObjectAt(brace) {
  return extractObject(source, brace);
}

function normalize(value) {
  return value.replaceAll(/[^a-z0-9]/gi, "").toLowerCase();
}

const actualZones = new Map();
for (const match of catalog.matchAll(/\["([^"]+)"\]\s*=\s*Zone\((\d+),\s*"([^"]*)"(?:,\s*([\d.]+))?\)/g)) {
  const points = new Map(match[3].split(";").filter(Boolean).map(item => {
    const [id, coordinates] = item.split(":");
    return [Number(id), coordinates];
  }));
  actualZones.set(match[1], { territoryId: Number(match[2]), points });
}

let failures = 0;
for (const required of [
  '"mobworldspawn"',
  '"sighting_set"',
  '"spawn_location"',
  'TryEnrichLocationAsync',
  '/api/app/data-center/',
  '"windows"',
  '"sightings"',
  '"zonePoiId"',
  'HasAuthoritativeTimestamp',
  'current location report',
  'matchingStartedAt - TimeSpan.FromMinutes(1)',
]) {
  if (!client.includes(required)) {
    console.error(`Faloop direct-feed enrichment audit: ${clientPath} is missing ${required}`);
    failures++;
  }
}
for (const required of [
  'new(2966, 139, 15, "Nandi")',
  '134, 135, 137, 138, 139, 180',
  'ResolveUniqueName',
]) {
  if (!huntCatalog.includes(required)) {
    console.error(`Centurio/Nandi audit: ${huntCatalogPath} is missing ${required}`);
    failures++;
  }
}
if (client.includes('/api/app/datacenter/')) {
  console.error('Faloop endpoint audit: obsolete /api/app/datacenter/ route is still present');
  failures++;
}
if (client.includes('Math.Abs((anchor - feedEvent.OccurredAtUtc).TotalMinutes) > 15')) {
  console.error('Faloop direct-feed enrichment audit: synthetic receipt time is still being used as a spawn-cycle cutoff');
  failures++;
}
let expectedPoiCount = 0;
for (const [normalizedSlug, actual] of actualZones) {
  const zoneMarker = new RegExp(`([a-z0-9_]+):\\{id:"\\1"`, "g");
  let found;
  for (const match of source.matchAll(zoneMarker)) {
    if (normalize(match[1]) !== normalizedSlug) continue;
    const candidate = extractObjectAt(source.indexOf("{", match.index));
    if (candidate.includes("territoryId:") && candidate.includes("pois:")) {
      found = { slug: match[1], block: candidate };
      break;
    }
  }
  if (!found) {
    console.error(`Missing Faloop zone object for catalog key ${normalizedSlug}`);
    failures++;
    continue;
  }

  const sizeFactor = Number(found.block.match(/sizeFactor:(\d+)/)?.[1] ?? 100);
  const expected = new Map();
  for (const match of found.block.matchAll(/\{id:(\d+),/g)) {
    const poi = extractObject(found.block, match.index);
    if (!poi.includes('type:"mob"')) continue;
    const location = poi.match(/location:"(\d+),(\d+)"/);
    if (!location) continue;
    const x = Number((Number(location[1]) / (sizeFactor / 2) + 1).toFixed(1));
    const y = Number((Number(location[2]) / (sizeFactor / 2) + 1).toFixed(1));
    expected.set(Number(match[1]), `${x},${y}`);
  }
  expectedPoiCount += expected.size;
  for (const [id, coordinates] of expected) {
    if (actual.points.get(id) !== coordinates) {
      console.error(`${found.slug}/${id}: expected ${coordinates}, catalog ${actual.points.get(id) ?? "missing"}`);
      failures++;
    }
  }
  if (actual.points.size !== expected.size) {
    console.error(`${found.slug}: expected ${expected.size} mob POIs, catalog has ${actual.points.size}`);
    failures++;
  }
}

const territoryIds = new Set([...huntCatalog.matchAll(/new\(\d+,\s*(\d+),\s*\d+,\s*"[^"]+"\)/g)]
  .map(match => Number(match[1])));
for (const territoryId of territoryIds) {
  if (![...actualZones.values()].some(zone => zone.territoryId === territoryId)) {
    console.error(`Supported S-rank territory ${territoryId} has no Faloop zone catalog.`);
    failures++;
  }
}

let supportedSCount = 0;
let supportedSPoiReferences = 0;
let aglaopePois = [];
const mobPattern = /([a-z0-9_]+):\{id:"([a-z0-9_]+)",name:\{/g;
for (const match of source.matchAll(mobPattern)) {
  if (match[1] !== match[2]) continue;
  const block = extractObjectAt(source.indexOf("{", match.index));
  const header = block.slice(0, 1500);
  if (!header.includes('rank:"S"') ||
      !/expansionId:"(a_realm_reborn|heavensward|stormblood|shadowbringers|endwalker|dawntrail)"/.test(header))
    continue;
  const zoneSlugs = [...(block.match(/zoneIds:\[([^\]]+)\]/)?.[1] ?? "").matchAll(/"([^"]+)"/g)]
    .map(value => value[1]);
  const poiIds = [...block.matchAll(/zonePoiIds:\[([^\]]*)\]/g)]
    .flatMap(value => [...value[1].matchAll(/\d+/g)].map(id => Number(id[0])));
  if (zoneSlugs.length === 0 || poiIds.length === 0) continue;
  const relevantZones = zoneSlugs.map(slug => actualZones.get(normalize(slug))).filter(Boolean);
  if (!relevantZones.some(zone => territoryIds.has(zone.territoryId))) continue;
  supportedSCount++;
  supportedSPoiReferences += poiIds.length;
  if (match[1] === "aglaope") {
    const normalPhase = block.match(/phases:\[\{zonePoiIds:\[([^\]]+)\]/)?.[1] ?? "";
    aglaopePois = [...normalPhase.matchAll(/\d+/g)].map(id => Number(id[0])).sort((a, b) => a - b);
  }
  for (const poiId of poiIds) {
    // SS follow-up phases can reference a POI in another territory of the same expansion.
    // POI IDs are global, and the live event supplies the actual zone slug.
    if (![...actualZones.values()].some(zone => zone.points.has(poiId))) {
      console.error(`Supported S rank ${match[1]} references unmapped POI ${zoneSlugs.join("/")}/${poiId}`);
      failures++;
    }
  }
}

if (aglaopePois.length === 0) {
  console.error("Aglaope did not expose any spawn POIs in the current Faloop client.");
  failures++;
} else {
  console.log(`Aglaope POIs verified: ${aglaopePois.join(", ")}`);
}

if (failures > 0) {
  console.error(`Faloop catalog audit failed with ${failures} issue(s). Bundle: ${bundleUrl}`);
  process.exitCode = 1;
} else {
  console.log(`Faloop catalog audit passed: ${actualZones.size} supported zones, ${expectedPoiCount} mapped mob POIs, ` +
    `${supportedSCount} supported S/SS definitions, ${supportedSPoiReferences} S/SS POI references. Bundle: ${bundleUrl}`);
}

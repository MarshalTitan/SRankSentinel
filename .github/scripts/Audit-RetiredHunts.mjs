import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
const feed = JSON.parse(readFileSync('repo.json', 'utf8'));
assert.deepEqual(feed.map(x => x.InternalName), ['SRankSentinel'], 'Only the supported SRankSentinel identity may be advertised.');
for (const path of [
  '.github/workflows/sentinelhunts-build.yml',
  '.github/workflows/sentinelhunts-release.yml',
  '.github/scripts/Validate-SentinelHuntsPackage.ps1',
  'SentinelHunts/SentinelHunts.json',
]) assert.equal(existsSync(path), false, `Retired installation/publication path restored: ${path}`);
const project = readFileSync('SentinelHunts/SentinelHunts.csproj', 'utf8');
assert(project.includes('<Use_DalamudPackager>false</Use_DalamudPackager>'));
assert(project.includes('<IsPackable>false</IsPackable>'));
console.log('Retirement guard passed; ordinary SRankSentinel remains the only installable identity.');

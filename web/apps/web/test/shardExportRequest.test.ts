import type { ShardExportRequest, ShardSelection } from '@mineclaw/contracts';
import { describe, expect, it } from 'vitest';
import { exportRequest, initialForm, type ExportForm, type ExportPreset } from '@/features/shards/ExportDialog';

/** The request as the dialog built it before its form became one state object (kept verbatim as the reference). */
function reference(f: ExportForm, preset: ExportPreset): ShardExportRequest {
  const { mode, name, description, kinds, dimension, query, semantic, people, packVersion, knowledgeDocs, includeRules, includeWorld } = f;
  return {
    name,
    description: description || null,
    includeEmbeddings: f.embeddings,
    includeSummaries: f.summaries,
    selection: {
      mode,
      node: mode === 'subtree' ? (preset.node ?? null) : null,
      kinds: mode === 'filter' ? (kinds as ShardSelection['kinds']) : null,
      dimension: mode === 'filter' && dimension !== '' ? Number(dimension) : null,
      query: mode === 'filter' && query ? query : null,
      semantic: mode === 'filter' && semantic,
      people: mode === 'person' ? people.split(',').map((p) => p.trim()).filter(Boolean) : null,
      packVersion: mode === 'pack' ? packVersion : null,
      includeKnowledgeDocs: mode === 'pack' && knowledgeDocs,
      includeRules,
      includeWorld,
    },
  };
}

const PRESETS: ExportPreset[] = [{}, { node: 7 }, { people: ['kiwi', 'fox'] }, { mode: 'pack' }, { kinds: ['rule'], query: 'melons' }];

describe('the shard export request', () => {
  it('starts from the preset as before', () => {
    expect(PRESETS.map((p) => initialForm(p))).toMatchSnapshot();
  });

  it('sends the same JSON (field order too) for every mode and option', () => {
    for (const preset of PRESETS)
      for (const mode of ['filter', 'person', 'pack', 'subtree'] as const)
        for (const flags of [0, 1, 2, 3])
          for (const dimension of ['', '0', '-1']) {
            const f: ExportForm = {
              ...initialForm(preset),
              mode,
              description: flags & 1 ? 'about melons' : '',
              dimension,
              query: flags & 2 ? 'cobble' : '',
              semantic: !!(flags & 1),
              people: flags & 2 ? ' kiwi, ,Robin ' : '',
              knowledgeDocs: !!(flags & 2),
              includeRules: !!(flags & 1),
              includeWorld: !!(flags & 2),
              embeddings: !(flags & 1),
              summaries: !(flags & 2),
            };
            expect(JSON.stringify(exportRequest(f, preset))).toBe(JSON.stringify(reference(f, preset)));
          }
  });
});

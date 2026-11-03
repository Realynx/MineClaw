import { describe, expect, it } from 'vitest';
import {
  AdminSettingsResponse,
  AdminSettingsSaveRequest,
  ROUTES,
  StepUpRequest,
  adminSettingsAuditSummary,
  toCamel,
  type RouteId,
} from '../src/index.js';

describe('administration contracts', () => {
  it('every write, test and restart needs a step-up; reads do not', () => {
    const ids = Object.keys(ROUTES).filter((k) => k.startsWith('admin.') && ROUTES[k as RouteId].bridge?.path.startsWith('/v1/admin/nodes')) as RouteId[];
    expect(ids.length).toBeGreaterThanOrEqual(6);
    for (const id of ids) {
      const r = ROUTES[id];
      expect(r.access, id).toBe('admin');
      expect(Boolean((r as { stepUp?: boolean }).stepUp), id).toBe(r.method === 'POST');
    }
  });

  it('the audit summary names keys and the reason, never a value', () => {
    const s = adminSettingsAuditSummary({
      changes: [
        { key: 'Agent:Llm:ApiKey', value: 'sk-ant-api03-secret' },
        { key: 'Agent:Llm:MainModel', value: 'claude-opus-5-5' },
      ],
      reason: 'rotate',
    });
    expect(s).toBe('keys: Agent:Llm:ApiKey, Agent:Llm:MainModel; reason: rotate');
    expect(adminSettingsAuditSummary({ kind: 'anthropic', apiKey: 'sk-ant-secret' })).toBe('test anthropic');
    expect(adminSettingsAuditSummary({ reason: 'apply', force: true })).toBe('forced; reason: apply');
  });

  it('step-up takes a code or a password, not both', () => {
    expect(StepUpRequest.safeParse({ code: '123456' }).success).toBe(true);
    expect(StepUpRequest.safeParse({ password: 'x' }).success).toBe(true);
    expect(StepUpRequest.safeParse({ code: '123456', password: 'x' }).success).toBe(false);
    expect(StepUpRequest.safeParse({}).success).toBe(false);
    expect(StepUpRequest.safeParse({ code: '12345' }).success).toBe(false);
  });

  it('a save needs a reason and at least one change', () => {
    expect(AdminSettingsSaveRequest.safeParse({ changes: [{ key: 'Hive:MaxClones', value: 3 }], reason: 'more bodies' }).success).toBe(true);
    expect(AdminSettingsSaveRequest.safeParse({ changes: [], reason: 'nothing' }).success).toBe(false);
    expect(AdminSettingsSaveRequest.safeParse({ changes: [{ key: 'Hive:MaxClones', value: 3 }], reason: ' ' }).success).toBe(false);
    expect(AdminSettingsSaveRequest.safeParse({ changes: [{ key: 'x', value: { nested: 1 } }], reason: 'nope' }).success).toBe(false);
  });

  it("parses the bot's snake_case settings view", () => {
    const wire = {
      node: { id: 'local', name: 'maid_chan', started_at: '2026-11-03T10:00:00+00:00', supervisor_url: 'http://127.0.0.1:8765', reachable: true },
      groups: [{ id: 'models', title: 'Models & providers', description: 'x' }],
      settings: [
        {
          key: 'Agent:Llm:ApiKey', group: 'models', section: 'Anthropic', label: 'Anthropic API key', description: 'x', type: 'secret',
          apply: 'restart', secret: true, editable: true, secret_state: { set: true, hint: '…ab12' }, source: 'override', overridden: true,
          pending_restart: true,
        },
        {
          key: 'Agent:Llm:MainModel', group: 'models', section: 'Tiers', label: 'Brain model', description: 'x', type: 'model', apply: 'restart',
          secret: false, editable: true, default: 'claude-opus-5-5', value: 'claude-opus-5-5', source: 'config', overridden: false,
          pending_restart: false, options: ['claude-opus-5-5'], provider_key: 'Agent:Llm:MainProvider',
        },
        {
          key: 'Agent:Chat:Aliases', group: 'bot', section: 'Identity', label: 'Aliases', description: 'x', type: 'list', apply: 'restart',
          secret: false, editable: true, value: ['claude'], default: ['claude chan'], source: 'default', overridden: false, pending_restart: false,
        },
      ],
      allowed_claude_models: ['claude-opus-5-5'],
      restart_pending: ['Agent:Llm:ApiKey'],
      override: { path: '/var/lib/mineclaw/settings.override.json', exists: true, updated_at: '2026-11-03T10:00:00+00:00', backups: 2 },
    };
    const r = AdminSettingsResponse.safeParse(toCamel(wire));
    if (!r.success) throw new Error(JSON.stringify(r.error.issues));
    expect(r.data.settings[0]!.secretState?.hint).toBe('…ab12');
    expect(r.data.settings[1]!.providerKey).toBe('Agent:Llm:MainProvider');
    expect(r.data.settings[0]!.value).toBeUndefined();
  });
});

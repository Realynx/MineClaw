import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { AdminSetting, SettingValue } from '@mineclaw/contracts';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { AdminFormContext, type AdminForm } from '@/features/administration/context';
import { buildDiff, formatValue, reasonError, toChanges, withoutKeys } from '@/features/administration/diff';
import {
  parseList,
  providerOf,
  secretView,
  serverErrors,
  shownValue,
  stepUpFresh,
  validateDraft,
  validateValue,
  type Draft,
} from '@/features/administration/fieldState';
import { ReviewBar } from '@/features/administration/ReviewBar';
import { SettingField } from '@/features/administration/SettingField';
import { mockSignIn, mockStepUp, resolvers } from '@/mocks/api';
import * as adminMock from '@/mocks/data/administration';

const SECRET = 'sk-ant-api03-mockmockmockab12';

function settings(): AdminSetting[] {
  adminMock.resetAdministration();
  return adminMock.settings().settings;
}

const byKey = (all: AdminSetting[], key: string) => all.find((s) => s.key === key)!;
const ALLOWED = adminMock.ALLOWED_CLAUDE;

describe('secret field states', () => {
  const all = settings();
  const key = byKey(all, 'Agent:Llm:ApiKey');

  it('a set secret shows only its last four', () => {
    expect(key.value).toBeNull();
    expect(key.secretState).toEqual({ set: true, hint: '…ab12' });
    expect(secretView(key, {})).toEqual({ mode: 'set', text: 'Set — ends in ab12' });
  });

  it('unset, replacing, clearing and reset', () => {
    const unset = byKey(all, 'Agent:Llm:Ollama:ApiKey');
    expect(secretView(unset, {}).mode).toBe('unset');
    expect(secretView(unset, {}).text).toBe('Not set');
    expect(secretView(key, { [key.key]: 'sk-ant-new' })).toEqual({
      mode: 'replacing',
      text: 'New value entered (hidden)',
    });
    expect(secretView(key, { [key.key]: '' }).mode).toBe('clearing');
    expect(secretView(key, { [key.key]: null }).mode).toBe('reset');
  });

  it('never shows a secret value in a field', () => {
    expect(shownValue(key, {})).toBeNull();
  });
});

describe('validation', () => {
  const all = settings();

  it('bounds, whole numbers and options', () => {
    const per = byKey(all, 'Supervisor:Viewer:PerMinute');
    expect(validateValue(per, 12)).toBeNull();
    expect(validateValue(per, 0)).toBe('at least 1');
    expect(validateValue(per, 500)).toBe('at most 120');
    expect(validateValue(per, 1.5)).toBe('a whole number');
    expect(validateValue(per, Number.NaN)).toBe('a number');
    expect(validateValue(byKey(all, 'Host:ConsoleLogLevel'), 'Loud')).toMatch(/one of/);
    expect(validateValue(byKey(all, 'Agent:Llm:Ollama:Endpoint'), 'ftp://x')).toMatch(/http/);
    expect(validateValue(byKey(all, 'Agent:Llm:Ollama:Endpoint'), '')).toBeNull();
    expect(validateValue(byKey(all, 'Agent:Chat:Aliases'), ['a', ' '])).toBe('no empty entries');
    expect(validateValue(byKey(all, 'Supervisor:Token'), 'x')).toBe('read-only');
    expect(validateValue(per, null)).toBeNull();
  });

  it('a Claude tier only takes 5.5-generation ids; an Ollama tier takes any tag', () => {
    const model = byKey(all, 'Agent:Llm:CheapModel');
    expect(validateValue(model, 'claude-haiku-5-5', { provider: 'anthropic', allowedClaude: ALLOWED })).toBeNull();
    expect(validateValue(model, 'claude-3-haiku', { provider: 'anthropic', allowedClaude: ALLOWED })).toMatch(
      /5\.5-generation/,
    );
    expect(validateValue(model, 'qwen3.5:32b', { provider: 'ollama', allowedClaude: ALLOWED })).toBeNull();
  });

  it('switching a tier to Ollama asks for a model the new provider has', () => {
    const draft: Draft = { 'Agent:Llm:CheapProvider': 'ollama' };
    expect(providerOf(byKey(all, 'Agent:Llm:CheapModel'), all, draft)).toBe('ollama');
    expect(validateDraft(all, draft, ALLOWED)).toEqual({});
    const back: Draft = { 'Agent:Llm:CheapProvider': 'anthropic', 'Agent:Llm:CheapModel': 'qwen3.5:32b' };
    expect(Object.keys(validateDraft(all, back, ALLOWED))).toEqual(['Agent:Llm:CheapModel']);
  });

  it('maps server details to setting keys', () => {
    expect(
      serverErrors(
        [
          { path: 'Agent:Llm:MainModel', message: 'bad' },
          { path: 'changes.1.value', message: 'worse' },
        ],
        ['a', 'b'],
      ),
    ).toEqual({
      'Agent:Llm:MainModel': 'bad',
      b: 'worse',
    });
  });

  it('lists, reasons and step-up freshness', () => {
    expect(parseList('a, b\n\n c ')).toEqual(['a', 'b', 'c']);
    expect(reasonError('ok')).toMatch(/at least 3/);
    expect(reasonError('fine')).toBeNull();
    expect(stepUpFresh({ stepUpUntil: new Date(Date.now() + 60_000).toISOString() })).toBe(true);
    expect(stepUpFresh({ stepUpUntil: new Date(Date.now() - 1).toISOString() })).toBe(false);
    expect(stepUpFresh({ stepUpUntil: null })).toBe(false);
  });
});

describe('the review diff', () => {
  const all = settings();

  it('lists real changes only, secrets as changed / cleared', () => {
    const draft: Draft = {
      'Agent:Llm:CheapModel': 'claude-sonnet-5-5',
      'Agent:Llm:MainModel': 'claude-opus-5-5', // unchanged: dropped
      'Agent:Llm:ApiKey': 'sk-ant-api03-brand-new-key-9999',
      'Agent:Llm:Ollama:ApiKey': '', // clearing an unset secret: nothing to do
      'Supervisor:Viewer:PerMinute': 20,
    };
    const rows = buildDiff(all, draft);
    expect(rows.map((r) => r.key)).toEqual(['Agent:Llm:ApiKey', 'Agent:Llm:CheapModel', 'Supervisor:Viewer:PerMinute']);
    const key = rows[0]!;
    expect(key).toMatchObject({ kind: 'secret-changed', from: 'set', to: 'changed', apply: 'restart' });
    expect(JSON.stringify(rows)).not.toContain('brand-new');
    expect(rows[2]).toMatchObject({ from: '12', to: '20', apply: 'live' });
    expect(toChanges(rows, draft)).toEqual([
      { key: 'Agent:Llm:ApiKey', value: 'sk-ant-api03-brand-new-key-9999' },
      { key: 'Agent:Llm:CheapModel', value: 'claude-sonnet-5-5' },
      { key: 'Supervisor:Viewer:PerMinute', value: 20 },
    ]);
    expect(buildDiff(all, { 'Agent:Llm:ApiKey': '' })[0]).toMatchObject({ kind: 'secret-cleared', to: 'cleared' });
    expect(withoutKeys(draft, ['Agent:Llm:ApiKey'])).not.toHaveProperty('Agent:Llm:ApiKey');
  });

  it('a reset of a value set here goes back to the base config', () => {
    adminMock.resetAdministration();
    adminMock.save([{ key: 'Supervisor:Viewer:PerMinute', value: 30 }], 'test', 'fox (web)');
    const list = adminMock.settings().settings;
    const per = byKey(list, 'Supervisor:Viewer:PerMinute');
    expect(per.overridden).toBe(true);
    expect(buildDiff(list, { [per.key]: null })[0]).toMatchObject({ kind: 'reset', from: '30', to: 'base config' });
    expect(buildDiff(settings(), { [per.key]: null })).toEqual([]); // nothing overridden: nothing to reset
  });

  it('formats values', () => {
    expect(formatValue(true)).toBe('on');
    expect(formatValue([])).toBe('(none)');
    expect(formatValue(null, { type: 'bool', nullable: true, unit: null })).toBe('(inherit)');
    expect(formatValue(50, { type: 'number', unit: 'USD', nullable: null })).toBe('50 USD');
  });

  it('the review bar shows the diff and never the secret', () => {
    const draft: Draft = { 'Agent:Llm:ApiKey': SECRET + 'X', 'Agent:Llm:CheapModel': 'claude-sonnet-5-5' };
    const rows = buildDiff(all, draft);
    const onSave = vi.fn();
    const { rerender } = render(
      <ReviewBar
        rows={rows}
        invalid={0}
        reason=""
        onReason={() => {}}
        onSave={onSave}
        onDiscard={() => {}}
        saving={false}
        error={null}
        open={false}
        onOpenChange={() => {}}
      />,
    );
    expect(screen.getByRole('region', { name: 'Unsaved changes' })).toHaveTextContent('2 unsaved changes');
    expect(screen.getByRole('region', { name: 'Unsaved changes' })).toHaveTextContent('2 need a restart');
    rerender(
      <ReviewBar
        rows={rows}
        invalid={0}
        reason="new key"
        onReason={() => {}}
        onSave={onSave}
        onDiscard={() => {}}
        saving={false}
        error={null}
        open
        onOpenChange={() => {}}
      />,
    );
    const table = screen.getByRole('table', { name: 'Changes' });
    expect(within(table).getByText('changed')).toBeInTheDocument();
    expect(within(table).getByText('claude-sonnet-5-5')).toBeInTheDocument();
    expect(document.body.textContent).not.toContain(SECRET);
    fireEvent.click(screen.getByRole('button', { name: 'Save 2 changes' }));
    expect(onSave).toHaveBeenCalledOnce();
  });

  it('the review bar refuses to save without a reason, and with invalid fields', () => {
    const rows = buildDiff(all, { 'Supervisor:Viewer:PerMinute': 20 });
    render(
      <ReviewBar
        rows={rows}
        invalid={1}
        reason=""
        onReason={() => {}}
        onSave={() => {}}
        onDiscard={() => {}}
        saving={false}
        error={null}
        open
        onOpenChange={() => {}}
      />,
    );
    // The open (modal) dialog hides the bar from the accessibility tree.
    expect(screen.getByRole('region', { name: 'Unsaved changes', hidden: true })).toHaveTextContent(
      'fix 1 field first',
    );
    expect(screen.getByRole('button', { name: 'Review changes', hidden: true })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save 1 change' })).toBeDisabled();
  });
});

function Harness({ s, all, onDraft }: { s: AdminSetting; all: AdminSetting[]; onDraft: (d: Draft) => void }) {
  const [draft, setDraft] = useState<Draft>({});
  const form: AdminForm = {
    node: 'local',
    all,
    allowedClaude: ALLOWED,
    draft,
    errors: {},
    stepUp: { run: (fn) => fn(), dialog: null },
    setValue: (key: string, v: SettingValue | undefined) => {
      const next: Record<string, SettingValue> = { ...draft };
      if (v === undefined) delete next[key];
      else next[key] = v;
      setDraft(next);
      onDraft(next);
    },
  };
  return (
    <QueryClientProvider client={new QueryClient()}>
      <AdminFormContext.Provider value={form}>
        <SettingField s={s} />
      </AdminFormContext.Provider>
    </QueryClientProvider>
  );
}

describe('the secret field', () => {
  it('Set — ends in ab12 · Replace · Clear, and typing stays hidden', () => {
    const all = settings();
    const s = byKey(all, 'Agent:Llm:ApiKey');
    let draft: Draft = {};
    render(<Harness s={s} all={all} onDraft={(d) => (draft = d)} />);
    expect(screen.getByText('Set — ends in ab12')).toBeInTheDocument();
    expect(screen.getByText('Restart')).toBeInTheDocument();
    expect(screen.getByText('environment')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Clear Anthropic API key' }));
    expect(draft).toEqual({ 'Agent:Llm:ApiKey': '' });
    expect(screen.getByText('Will be cleared on save')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Undo Anthropic API key' }));
    fireEvent.click(screen.getByRole('button', { name: 'Replace Anthropic API key' }));
    const input = screen.getByLabelText('Anthropic API key');
    expect(input).toHaveAttribute('type', 'password');
    expect((input as HTMLInputElement).value).toBe('');
    fireEvent.change(input, { target: { value: 'sk-ant-xyz' } });
    expect(draft).toEqual({ 'Agent:Llm:ApiKey': 'sk-ant-xyz' });
  });

  it('a read-only secret shows its state and why it is locked', () => {
    const all = settings();
    render(<Harness s={byKey(all, 'Supervisor:Token')} all={all} onDraft={() => {}} />);
    expect(screen.getByText('Read-only')).toBeInTheDocument();
    expect(screen.getByText(/cut the explorer off/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Replace/ })).toBeNull();
    expect(document.body.textContent).not.toContain('f00dfeed');
  });

  it('a Claude model picker offers only the allowed ids', () => {
    const all = settings();
    render(<Harness s={byKey(all, 'Agent:Llm:MainModel')} all={all} onDraft={() => {}} />);
    const opts = within(screen.getByLabelText('Brain tier Model'))
      .getAllByRole('option')
      .map((o) => o.textContent);
    expect(opts).toEqual(ALLOWED);
  });
});

describe('mock server step-up', () => {
  beforeEach(() => {
    adminMock.resetAdministration();
    mockSignIn('fox');
  });

  it('settings never carry a secret value', () => {
    const out = JSON.stringify(resolvers['admin.settings']!({ params: { node: 'local' }, query: {}, body: null }));
    expect(out).not.toContain(SECRET);
    expect(out).not.toContain('f00dfeedf00dfeed');
  });

  it('auth.me reports the step-up window', () => {
    expect(
      (resolvers['auth.me']!({ params: {}, query: {}, body: null }) as { stepUpUntil: unknown }).stepUpUntil,
    ).toBeNull();
    mockStepUp(true);
    expect(
      (resolvers['auth.me']!({ params: {}, query: {}, body: null }) as { stepUpUntil: unknown }).stepUpUntil,
    ).toEqual(expect.any(String));
  });
});

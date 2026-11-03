'use client';

import type { AdminSetting, SettingValue } from '@mineclaw/contracts';
import { createContext, useContext } from 'react';
import type { Draft } from './fieldState';
import type { StepUp } from './useStepUp';

export interface AdminForm {
  node: string;
  all: readonly AdminSetting[];
  allowedClaude: readonly string[];
  draft: Draft;
  /** `undefined` = undo the edit; `null` = reset to the base config. */
  setValue: (key: string, value: SettingValue | undefined) => void;
  errors: Readonly<Record<string, string>>;
  stepUp: StepUp;
}

export const AdminFormContext = createContext<AdminForm | null>(null);

export function useAdminForm(): AdminForm {
  const v = useContext(AdminFormContext);
  if (!v) throw new Error('useAdminForm outside AdminFormContext');
  return v;
}

export function fieldId(key: string): string {
  return `setting-${key.replace(/[^A-Za-z0-9]+/g, '-')}`;
}

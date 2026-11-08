import {
  Activity,
  BookOpen,
  Castle,
  Boxes,
  Code2,
  Gauge,
  PackageOpen,
  Radio,
  Search,
  ShieldCheck,
  SlidersHorizontal,
  Smile,
  type LucideIcon,
} from 'lucide-react';
import type { RouteAccess } from '@mineclaw/contracts';

export interface NavItem {
  href: string;
  label: string;
  icon: LucideIcon;
  description: string;
  access?: RouteAccess;
  keywords?: string[];
}

export interface NavGroup {
  label: string;
  items: NavItem[];
}

export const NAV: NavGroup[] = [
  {
    label: 'Live',
    items: [
      { href: '/story', label: 'Controller', icon: Castle, description: 'Manage your colony, plans and pawns', keywords: ['story', 'colony', 'pawns', 'build', 'work', 'schedule'] },
      { href: '/live', label: 'Live hive', icon: Radio, description: 'Bots, thoughts, scripts and chat', keywords: ['bots', 'hive', 'world'] },
      { href: '/memory-activity', label: 'Memory activity', icon: Activity, description: 'Which memories each brain is using, as a map or a cloud', keywords: ['heat', 'raptor', 'context', 'tree', 'graph', 'cluster', 'cloud', 'umap'] },
    ],
  },
  {
    label: 'Memory',
    items: [
      { href: '/overview', label: 'Overview', icon: Gauge, description: 'Counts, database and RAPTOR stats' },
      { href: '/memory', label: 'Memory browser', icon: BookOpen, description: 'Every memory kind, paged', keywords: ['facts', 'claims', 'places', 'chat'] },
      { href: '/search', label: 'Search', icon: Search, description: "The bot's recall pipeline", keywords: ['recall', 'rag'] },
      { href: '/personality', label: 'Personality', icon: Smile, description: 'Traits, mood and history', keywords: ['self', 'traits', 'mood'] },
    ],
  },
  {
    label: 'Library',
    items: [
      { href: '/scripts', label: 'Scripts', icon: Code2, description: 'Saved scripts, versions and runs', keywords: ['lua', 'js', 'routines'] },
      { href: '/designs', label: 'Designs', icon: Boxes, description: 'Blueprints and revisions', keywords: ['blueprint', 'build'] },
      { href: '/shards', label: 'Shards', icon: PackageOpen, description: 'Knowledge packs, export and import', keywords: ['export', 'import', 'pack'] },
    ],
  },
  {
    label: 'Admin',
    items: [
      { href: '/admin', label: 'Edits & audit', icon: ShieldCheck, description: 'Rebuilds, backups and audit logs', access: 'admin', keywords: ['audit', 'rebuild', 'backup'] },
      {
        href: '/administration',
        label: 'Administration',
        icon: SlidersHorizontal,
        description: "The bot's own settings: API keys, models, Ollama, restart",
        access: 'admin',
        keywords: ['settings', 'api keys', 'models', 'ollama', 'restart', 'config'],
      },
    ],
  },
];

export const NAV_ITEMS: NavItem[] = NAV.flatMap((g) => g.items);

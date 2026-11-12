import { z } from 'zod';
import { QInt } from './common.js';

/** Deliberately separate from world entity / vision payloads: these resources are for human browsers only. */
export const HumanSkinSettings = z.object({ enabled: z.boolean(), cached: z.number().int().min(0), unavailable: z.number().int().min(0) });
export const HumanSkinSave = z.object({ enabled: z.boolean(), reason: z.string().trim().min(3).max(300) }).strict();
export type HumanSkinSave = z.infer<typeof HumanSkinSave>;
export const HumanSkinQuery = z.object({ epoch: z.string().max(64).optional(), since: QInt.min(0).default(0) });

/** The default player textures every viewer can show from the user's own imported game assets (never stored on the server). */
export const DefaultSkin = z.enum(['steve', 'alex']);
export type DefaultSkin = z.infer<typeof DefaultSkin>;

export const HumanSkinFeed = z.object({
  enabled: z.boolean(), epoch: z.string(), revision: z.number().int().min(0),
  skins: z.array(z.object({
    name: z.string().regex(/^[a-z0-9_]{1,16}$/), slim: z.boolean(), height: z.union([z.literal(32), z.literal(64)]), png: z.string().max(87384),
    /** A pawn given a default skin: the viewer draws its own imported Steve / Alex texture and `png` is empty. */
    base: DefaultSkin.optional(),
  })).max(512),
});

// ------------------------------------------------------------------ pawn skins (the Controller's Skin Browser)

/** Largest skin PNG accepted from a browser or a profile lookup (before ancillary chunks are stripped). */
export const SKIN_PNG_MAX_BYTES = 64 * 1024;
/** Skins the shared library keeps. */
export const SKIN_LIBRARY_MAX = 128;
/** Account names: what the world viewer keys players by (case-insensitive). */
export const SkinAccountName = z.string().regex(/^[A-Za-z0-9_]{1,16}$/);

export const SkinId = z.string().regex(/^[a-f0-9]{24}$/).describe('content hash of the stored PNG');
/** What a pawn wears: a library skin or a default skin. */
export const SkinRef = z.discriminatedUnion('kind', [
  z.object({ kind: z.literal('library'), id: SkinId }).strict(),
  z.object({ kind: z.literal('default'), id: DefaultSkin }).strict(),
]);
export type SkinRef = z.infer<typeof SkinRef>;

export const SkinLibraryEntry = z.object({
  id: SkinId,
  label: z.string().max(32),
  slim: z.boolean(),
  height: z.union([z.literal(32), z.literal(64)]),
  /** Base64 PNG (critical chunks only). */
  png: z.string().max(87384),
  source: z.enum(['upload', 'player']),
  createdAt: z.string(),
  createdBy: z.string().nullish(),
});
export type SkinLibraryEntry = z.infer<typeof SkinLibraryEntry>;

export const PawnSkins = z.object({
  library: z.array(SkinLibraryEntry).max(SKIN_LIBRARY_MAX),
  /** Lower-cased account name → its skin. Absent = the viewer's normal default appearance. */
  assignments: z.record(z.string(), SkinRef),
  /** Whether "Look up a player" can reach the official profile service (Administration → Player skins is on). */
  lookup: z.boolean(),
});
export type PawnSkins = z.infer<typeof PawnSkins>;

export const SkinUploadRequest = z.object({
  label: z.string().trim().min(1).max(32),
  slim: z.boolean().default(false),
  png: z.string().min(1).max(87384).regex(/^[A-Za-z0-9+/]+={0,2}$/, 'base64'),
}).strict();
export type SkinUploadRequest = z.infer<typeof SkinUploadRequest>;
export const SkinDeleteRequest = z.object({ id: SkinId }).strict();
export const SkinAssignRequest = z.object({
  pawns: z.array(SkinAccountName).min(1).max(64),
  /** null = back to the normal default appearance. */
  skin: SkinRef.nullable(),
}).strict();
export type SkinAssignRequest = z.infer<typeof SkinAssignRequest>;
export const SkinLookupRequest = z.object({ name: SkinAccountName }).strict();
export const SkinSaved = z.object({ skin: SkinLibraryEntry, skins: PawnSkins });

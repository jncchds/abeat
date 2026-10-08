/** A link shown next to each map version (beside Zip / ArcViewer). */
export interface VersionLink { label: string; href: string; title?: string }

/**
 * Machine-local UI extension: a git-ignored folder `src/local/<name>/` whose `index.ts` default-exports
 * this. Nothing under `src/local/` is part of the repo.
 */
export interface Extension {
  versionLinks?: (songId: string, versionId: string, zipUrl: string) => VersionLink[]
}

const modules = import.meta.glob<{ default: Extension }>('./local/*/index.ts', { eager: true })

export const extensions: Extension[] = Object.values(modules).map(m => m.default)

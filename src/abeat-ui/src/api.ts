import axios from 'axios'

export type SongStatus = 'Queued' | 'Analyzing' | 'Generating' | 'Ready' | 'Failed'

export interface AnalysisOptions {
  beatBackend: string
  stems: boolean
  bpmOverride?: number | null
}

export interface SongMeta {
  id: string
  fileName: string
  sourceUrl?: string | null
  createdUtc: string
  status: SongStatus
  error?: string | null
  title: string
  artist: string
  bpm?: number | null
  durationSec?: number | null
  analysis: AnalysisOptions
  referenceMapper?: string | null
  referenceUrl?: string | null
}

export interface Onset { t: number; s: number; br: number }
export interface Section { start: number; end: number; label: string; energy: number }

export interface Analysis {
  source: { path: string; url?: string | null; title: string; artist: string }
  audio: { file: string; durationSec: number; sampleRate: number; padSec: number }
  tempo: { bpm: number; firstBeatSec: number; residualMs: number; maxDevMs: number; stable: boolean; backend: string; beats: number[]; downbeats: number[] }
  energy: { hopSec: number; values: number[] }
  sections: Section[]
  layerSource: string
  layers: Record<string, Onset[]>
}

export interface Note { b: number; x: number; y: number; c: number; d: number }
export interface Bomb { b: number; x: number; y: number }
export interface Wall { b: number; d: number; x: number; y: number; w: number; h: number }
export interface Issue { b: number; hand: number; kind: string; cost: number }

export interface Report {
  notes: number
  nps: number
  peakNps: number
  resets: number
  bombResets: number
  visionBlocks: number
  crossovers: number
  handClashes: number
  wallClashes: number
  bombHits: number
  meanCost: number
  flowScore: number
  leftShare: number
  lights: number
  issues: Issue[]
}

export interface Difficulty {
  name: string
  njs: number
  offset: number
  jumpDistance: number
  notes: Note[]
  bombs: Bomb[]
  walls: Wall[]
  report: Report
}

export interface MapData { difficulties: Difficulty[] }

/** A saved generation, or the imported human map (id "human"). */
export type Version =
  | { id: 'human'; kind: 'human'; label: string; difficulties: string[] }
  | {
      id: string
      kind: 'abeat'
      createdUtc: string
      draft: boolean
      appVersion: string
      difficulties: string[]
      /** Note-timing F1 against the human map per difficulty (when there is one). */
      vsHuman: Record<string, number> | null
    }

/** Version A against version B (B is the reference for precision / recall). */
export interface Comparison {
  f1: number
  precision: number
  recall: number
  offsetMs: number
  directionDistance: number
  positionDistance: number
  aDoubles: number
  bDoubles: number
  aNps: number
  bNps: number
}

export interface GeneratorSettings {
  difficulties: string[]
  density: number
  seed: number
  beamWidth: number
  weights: Record<string, number>
  layerWeights: Record<string, number>
  lights: boolean
  walls: boolean
  dodgeWalls: boolean
  crouchWalls: boolean
  bombs: boolean
  [key: string]: unknown
}

export interface Defaults { settings: GeneratorSettings }

const http = axios.create({ baseURL: '/api' })

export const getConfig = () => http.get<{ httpsPort: number | null }>('/config')
export const getDefaults = () => http.get<Defaults>('/defaults')
export const getSongs = () => http.get<SongMeta[]>('/songs')
export const getSong = (id: string) => http.get<{ meta: SongMeta; log: string[] }>(`/songs/${id}`)
export const deleteSong = (id: string) => http.delete(`/songs/${id}`)
export const getAnalysis = (id: string) => http.get<Analysis>(`/songs/${id}/analysis`)
export const getSettings = (id: string) => http.get<GeneratorSettings>(`/songs/${id}/settings`)
export const generate = (id: string, s: GeneratorSettings, draft: boolean) =>
  http.post<{ version: Version; difficulties: Difficulty[] }>(`/songs/${id}/generate`, s, { params: { draft } })
export const getVersions = (id: string) => http.get<Version[]>(`/songs/${id}/versions`)
export const getVersion = (id: string, v: string) => http.get<MapData>(`/songs/${id}/versions/${v}`)
export const deleteVersion = (id: string, v: string) => http.delete(`/songs/${id}/versions/${v}`)
export const getVersionSettings = (id: string, v: string) => http.get<GeneratorSettings>(`/songs/${id}/versions/${v}/settings`)
export const compareVersions = (id: string, a: string, ad: string, b: string, bd: string) =>
  http.get<Comparison>(`/songs/${id}/compare`, { params: { a, ad, b, bd } })
export interface StemFile { name: string; file: string; bytes: number }
export const getStems = (id: string) => http.get<StemFile[]>(`/songs/${id}/stems`)
export const stemUrl = (id: string, file: string) => `/api/songs/${id}/stems/${encodeURIComponent(file)}`
export const reanalyze = (id: string, o: AnalysisOptions) => http.post<SongMeta>(`/songs/${id}/reanalyze`, o)

export function uploadSong(file: File, beats: string, stems: boolean) {
  const fd = new FormData()
  fd.append('file', file)
  fd.append('beats', beats)
  fd.append('stems', String(stems))
  return http.post<SongMeta>('/songs', fd)
}

export const addSongUrl = (url: string, beats: string, stems: boolean) =>
  http.post<SongMeta>('/songs/url', { url, beats, stems })

export const audioUrl = (id: string) => `/api/songs/${id}/audio`
export const coverUrl = (id: string) => `/api/songs/${id}/cover`
export const zipUrl = (id: string, version?: string) => `/api/songs/${id}/map.zip${version ? `?version=${encodeURIComponent(version)}` : ''}`

export function errorText(e: unknown): string {
  if (axios.isAxiosError(e)) return typeof e.response?.data === 'string' ? e.response.data : e.message
  return e instanceof Error ? e.message : String(e)
}

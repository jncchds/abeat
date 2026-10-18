import { useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { useLocation } from 'react-router-dom'
import { Marked, type Tokens } from 'marked'
import manual from '@manual?raw'
import YouTubeAccount from '../components/YouTubeAccount'

const REPO = 'https://github.com/jncchds/abeat/blob/main/docs/'

/** GitHub's heading anchors, which the manual's own links use: lower case, punctuation dropped, spaces to
 *  hyphens, "-1", "-2", ... for repeats. */
function slugger() {
  const seen = new Map<string, number>()
  return (text: string) => {
    const base = text.toLowerCase().trim().replace(/<[^>]+>/g, '').replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-')
    const n = seen.get(base) ?? 0
    seen.set(base, n + 1)
    return n ? `${base}-${n}` : base
  }
}

function render(md: string) {
  const slug = slugger()
  const marked = new Marked({
    gfm: true,
    renderer: {
      heading({ tokens, depth, text }: Tokens.Heading) {
        const id = slug(text.replace(/[`*_]/g, ''))
        return `<h${depth} id="${id}">${this.parser.parseInline(tokens)}</h${depth}>\n`
      },
      link({ href, title, tokens }: Tokens.Link) {
        const inner = this.parser.parseInline(tokens)
        const t = title ? ` title="${title}"` : ''
        if (href.startsWith('#')) return `<a href="${href}"${t}>${inner}</a>`
        // files next to the manual in the repository open on GitHub
        const url = /^[a-z]+:/i.test(href) ? href : new URL(href, REPO).href
        return `<a href="${url}"${t} target="_blank" rel="noopener noreferrer">${inner}</a>`
      },
    },
  })
  return marked.parse(md, { async: false })
}

/** docs/MANUAL.md, bundled into the UI at build time. */
export default function ManualPage() {
  const html = useMemo(() => render(manual), [])
  const location = useLocation()
  const ref = useRef<HTMLElement>(null)
  // live controls placed where the manual has their placeholder (an empty <div> on GitHub)
  const [youtubeSlot, setYoutubeSlot] = useState<Element | null>(null)
  useEffect(() => setYoutubeSlot(ref.current?.querySelector('[data-youtube-signin]') ?? null), [html])

  useEffect(() => {
    const id = decodeURIComponent(location.hash.slice(1))
    if (id) document.getElementById(id)?.scrollIntoView()
    else ref.current?.scrollIntoView()
  }, [location.hash])

  return (
    <>
      <article ref={ref} className="page manual" dangerouslySetInnerHTML={{ __html: html }} />
      {youtubeSlot && createPortal(<YouTubeAccount />, youtubeSlot)}
    </>
  )
}

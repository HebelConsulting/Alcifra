// Demo board minutes for the Crypto tenant's sample data ("Board minutes 2026-03", CryptoDemoSeeder).
//   SOURCE_DATE_EPOCH=1772496000 typst compile --ignore-system-fonts crypto-board-minutes.typ crypto-board-minutes.pdf
//
// The Crypto tenant shows that content leaves only as an envelope the reader's key opens, so its two sample
// documents are named to sound confidential. They used to carry the Acme invoice and offer as placeholder
// content, which read as a mistake to anyone who opened them; these say what their names promise. A fictional
// company and placeholder names, bilingual like the rest of the demo set. Typst's bundled fonts only and a fixed
// SOURCE_DATE_EPOCH, so the committed PDF is exactly what this source builds.

#set document(title: "Board minutes 2026-03")
#set page(paper: "a4", margin: (x: 2.2cm, y: 2cm), numbering: "1 / 1")
#set text(font: "Libertinus Serif", size: 10.5pt, lang: "de")
#let accent = rgb("#5b2a86") // violet: the Crypto tenant's documents read apart from the Acme set at a glance

#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #grid(columns: (1fr, auto), align: (left + horizon, right + horizon),
    [#text(size: 19pt, weight: "bold")[Kristall Holding AG] \ #text(size: 9pt)[Verwaltungsrat / Board of Directors]],
    [#text(size: 9pt)[Seestrasse 4 · 6004 Luzern \ VERTRAULICH / CONFIDENTIAL]],
  )
]

#v(16pt)
#text(size: 16pt, weight: "bold")[Protokoll der Verwaltungsratssitzung]
#v(-6pt)
#text(size: 11pt, fill: luma(90))[Minutes of the board meeting · 3 March 2026]

#v(10pt)
#grid(columns: (auto, 1fr), column-gutter: 14pt, row-gutter: 6pt,
  [*Ort / Place*], [Sitzungszimmer Pilatus, Luzern · 14:00–16:30],
  [*Anwesend / Present*], [A. Muster (Präsident / Chair), B. Beispiel, C. Exempel, D. Probe],
  [*Protokoll / Minutes*], [E. Vorlage (Sekretariat / Secretary)],
  [*Entschuldigt / Apologies*], [—],
)

#v(12pt)
#line(length: 100%, stroke: 0.5pt + luma(180))

#let item(number, de, en, body) = {
  v(8pt)
  text(weight: "bold")[#number. #de]
  text(fill: luma(90))[ \/ #en]
  v(-4pt)
  body
}

#item(1, "Genehmigung des letzten Protokolls", "Approval of the last minutes")[
  Das Protokoll vom 2. Dezember 2025 wird ohne Änderungen genehmigt.
  _The minutes of 2 December 2025 are approved without changes._
]

#item(2, "Jahresabschluss 2025", "Annual accounts 2025")[
  Der Finanzchef erläutert den Abschluss. Umsatz CHF 18.4 Mio. (+6 %), Betriebsergebnis CHF 2.1 Mio.
  Der Verwaltungsrat genehmigt den Abschluss zuhanden der Generalversammlung.
  _The board approves the 2025 accounts for submission to the general meeting._
]

#item(3, "Lohnrunde 2026", "Salary review 2026")[
  Die Geschäftsleitung beantragt eine Lohnsumme von +2.4 %. Die Einzelheiten stehen im separaten,
  vertraulichen Dokument „Salary review 2026“. Der Verwaltungsrat stimmt dem Antrag zu.
  _The board approves a salary budget of +2.4 %; details in the separate confidential document._
]

#item(4, "Standort Zug", "Zug office")[
  Die Miete der Büroräume in Zug wird um drei Jahre verlängert. B. Beispiel enthält sich der Stimme.
  _The Zug office lease is extended by three years; one abstention._
]

#item(5, "Verschiedenes", "Any other business")[
  Nächste Sitzung: 16. Juni 2026. _Next meeting: 16 June 2026._
]

#v(1fr)
#grid(columns: (1fr, 1fr), column-gutter: 30pt,
  [#line(length: 100%, stroke: 0.5pt) A. Muster, Präsident / Chair],
  [#line(length: 100%, stroke: 0.5pt) E. Vorlage, Protokoll / Minutes],
)
#v(6pt)
#text(size: 8pt, fill: luma(120))[Fiktives Demo-Dokument von Alcifra. / A fictional Alcifra demo document.]

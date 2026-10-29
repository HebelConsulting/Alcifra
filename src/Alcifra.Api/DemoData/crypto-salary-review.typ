// Demo salary review for the Crypto tenant's sample data ("Salary review 2026", CryptoDemoSeeder).
//   SOURCE_DATE_EPOCH=1768348800 typst compile --ignore-system-fonts crypto-salary-review.typ crypto-salary-review.pdf
//
// The companion of crypto-board-minutes.typ (whose header says why both exist): the confidential detail the
// minutes' item 3 refers to. Fictional company, roles rather than people, bilingual. Bundled fonts and a fixed
// SOURCE_DATE_EPOCH, so the committed PDF is exactly what this source builds.

#set document(title: "Salary review 2026")
#set page(paper: "a4", margin: (x: 2.2cm, y: 2cm), numbering: "1 / 1")
#set text(font: "Libertinus Serif", size: 10.5pt, lang: "de")
#let accent = rgb("#5b2a86") // the Crypto tenant's violet, as in the board minutes

#block(fill: accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
  #set text(fill: white)
  #grid(columns: (1fr, auto), align: (left + horizon, right + horizon),
    [#text(size: 19pt, weight: "bold")[Kristall Holding AG] \ #text(size: 9pt)[Personal / Human Resources]],
    [#text(size: 9pt)[Seestrasse 4 · 6004 Luzern \ STRENG VERTRAULICH / STRICTLY CONFIDENTIAL]],
  )
]

#v(16pt)
#text(size: 16pt, weight: "bold")[Lohnrunde 2026]
#v(-6pt)
#text(size: 11pt, fill: luma(90))[Salary review 2026 · Antrag an den Verwaltungsrat / Proposal to the board · 14 January 2026]

#v(10pt)
Die Geschäftsleitung beantragt eine Erhöhung der Lohnsumme um *2.4 %* per 1. April 2026: 1.6 % generell,
0.8 % individuell nach Leistung. Die Teuerung 2025 betrug 0.7 %.
_Management proposes a salary budget of +2.4 % from 1 April 2026: 1.6 % across the board, 0.8 % by
performance. Inflation in 2025 was 0.7 %._

#v(10pt)
#let header(de, en) = [*#de* \ #text(size: 8.5pt, fill: luma(90))[#en]]
#table(
  columns: (2.2fr, 0.8fr, 1.2fr, 0.9fr, 1.2fr),
  stroke: (x, y) => if y == 0 { (bottom: 0.8pt + accent) } else { (bottom: 0.3pt + luma(200)) },
  inset: (x: 6pt, y: 6pt),
  align: (x, y) => if x == 0 { left } else { right },
  header("Funktion", "Role"), header("Stellen", "Headcount"), header("Lohn heute", "Salary today"),
  header("Antrag", "Proposed"), header("Lohn neu", "New salary"),
  [Geschäftsleitung / Executive board], [3], [CHF 214 000], [+2.0 %], [CHF 218 280],
  [Teamleitung / Team leads], [7], [CHF 142 000], [+2.4 %], [CHF 145 408],
  [Fachspezialist:innen / Specialists], [24], [CHF 108 500], [+2.6 %], [CHF 111 321],
  [Sachbearbeitung / Clerical staff], [18], [CHF 79 000], [+2.8 %], [CHF 81 212],
  [Lernende / Apprentices], [6], [CHF 14 400], [+3.0 %], [CHF 14 832],
)
#text(size: 8.5pt, fill: luma(110))[Durchschnittliche Jahreslöhne, brutto. / Average gross annual salaries.]

#v(10pt)
*Begründung / Rationale.* Die Fluktuation bei den Fachspezialist:innen lag 2025 bei 11 %; der Antrag
gewichtet diese Gruppe und die tieferen Lohnklassen stärker.
_Turnover among specialists was 11 % in 2025; the proposal weights that group and the lower salary bands._

#v(1fr)
#grid(columns: (1fr, 1fr), column-gutter: 30pt,
  [#line(length: 100%, stroke: 0.5pt) Leitung Personal / Head of HR],
  [#line(length: 100%, stroke: 0.5pt) Genehmigt / Approved, 3.3.2026],
)
#v(6pt)
#text(size: 8pt, fill: luma(120))[Fiktives Demo-Dokument von Alcifra. / A fictional Alcifra demo document.]

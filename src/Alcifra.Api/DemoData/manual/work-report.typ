// "Alpenglas work report": the Manual tenant's SIGNING DEMO (mobile #364, core #2489). The narrated videos' signing
// segment ticks its checkboxes, writes a remark in the remarks box, signs for the customer and embeds everything
// into a new version — so this document exercises every annotation kind in one act. Bilingual like every sample
// (German primary, English secondary). Built by build.sh; seeded by KristallActivity.SigningDemoAsync.
#import "style.typ": *

#let alpenglas = (name: "Alpenglas AG", address: "Industriestrasse 12 · 6010 Kriens", accent: rgb("#1f6f8b"))

// An empty checkbox the technician ticks — the film's annotations land inside these squares.
#let item-box = rect(width: 9pt, height: 9pt, stroke: 0.7pt + luma(55))

// A fill-in line for the record block.
#let fill-line(label, width: 60%) = grid(columns: (auto, 1fr), column-gutter: 8pt,
  text(size: 10pt)[#label], line(length: width, stroke: 0.5pt + luma(120)))

// A realistic signature field: a caption above a bordered box to sign inside (not a full-width rule).
#let signature-field(label, height: 44pt) = grid(columns: (1fr,), rows: 2, row-gutter: 4pt,
  text(size: 8.5pt, fill: luma(110))[#label],
  block(width: 100%, height: height, stroke: 0.6pt + luma(120), radius: 2pt),
)

#show: sample.with(title: "Arbeitsrapport / Work report AR-2026-014", lang: "de")

#letterhead(alpenglas, [Glas- und Fassadenbau / Glass and façades], [MWST CHE-123.456.789])

#addressed([Kristall Holding AG \ Hausdienst / Facilities \ Seestrasse 4 \ 6004 Luzern],
    [*Arbeitsrapport / Work report*], [AR-2026-014], [*Datum / Date*], [10.03.2026],
    [*Objekt / Site*], [Seestrasse 4, Eingangsfassade], [*Auftrag / Order*], [Offerte 2026-022])

#v(10pt)
#heading-pair("Ausgeführte Arbeiten", "Work performed")
#grid(columns: (auto, 1fr), column-gutter: 12pt, row-gutter: 13pt,
  item-box, [Alte Fenster ausgebaut. _Old windows removed._],
  item-box, [Sechs Fassadenverglasungen montiert. _Six façade panes installed._],
  item-box, [Fugen neu abgedichtet. _Joints re-sealed._],
  item-box, [Baustelle gereinigt und Abfall entsorgt. _Site cleaned, waste disposed._],
)

#v(12pt)
#heading-pair("Rapport", "Record")
#grid(columns: (1fr,), row-gutter: 14pt,
  fill-line([Arbeitsstunden / Working hours], width: 45%),
  fill-line([Material / Materials], width: 70%),
  fill-line([Fahrzeit / Travel], width: 35%),
)

#v(12pt)
#heading-pair("Bemerkungen", "Remarks")
#block(width: 100%, height: 64pt, stroke: 0.5pt + luma(150), inset: 8pt, [])

#v(1fr)
Die Arbeiten wurden zu unserer Zufriedenheit ausgeführt. \
_The works were carried out to our satisfaction._

#v(8pt)
#grid(columns: (1fr, 110pt), column-gutter: 26pt,
  signature-field([Unterschrift Kunde / Customer signature]),
  signature-field([Datum / Date]),
)

// A scanned-looking delivery note waiting in the Accounting group's intray (filing from the intray).
#import "style.typ": *
#let alpenglas = (name: "Alpenglas AG", address: "Industriestrasse 12 · 6010 Kriens", accent: rgb("#1f6f8b"))
#show: sample.with(title: "Lieferschein LS-2026-0417")
#letterhead(alpenglas, [Glas- und Fassadenbau / Glass and façades], [Lieferschein / Delivery note])
#facts([*Lieferschein / Delivery note*], [LS-2026-0417], [*Datum / Date*], [17.04.2026],
  [*Empfänger / Recipient*], [Kristall Holding AG, Seestrasse 4, 6004 Luzern])
#v(12pt)
#table(columns: (1fr, auto), stroke: 0.4pt, [*Artikel / Article*], [*Menge / Qty*],
  [Ersatzdichtung Schiebetür / Sliding door seal], [4], [Türschliesser / Door closer], [1])
#signatures([Erhalten / Received], [Alpenglas AG])

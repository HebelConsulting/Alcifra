// "Alpenglas offer 2026-022" in TWO revisions, so "Compare versions" has real text to diff:
// build.sh compiles it with --input rev=1 and rev=2 (a changed quantity, an added item, new totals).
#import "style.typ": *
#let v2 = sys.inputs.at("rev", default: "1") == "2"
#let alpenglas = (name: "Alpenglas AG", address: "Industriestrasse 12 · 6010 Kriens", accent: rgb("#1f6f8b"))
#show: sample.with(title: "Offerte 2026-022")
#letterhead(alpenglas, [Glas- und Fassadenbau / Glass and façades], [MWST CHE-123.456.789])

#addressed([Kristall Holding AG \ Facility Management \ Seestrasse 4 \ 6004 Luzern], [*Offerte / Offer*], [2026-022], [*Revision*], [#if v2 [2] else [1]],
    [*Datum / Date*], [#if v2 [20.02.2026] else [09.02.2026]], [*Gültig bis / Valid until*], [31.03.2026])
#v(14pt)
#heading-pair("Offerte Eingangsfassade", "Offer for the entrance façade")
#if v2 [Gemäss Ihrer Rückmeldung vom 18. Februar haben wir die Offerte angepasst.
  _Revised following your feedback of 18 February._]
else [Besten Dank für Ihre Anfrage. Gerne offerieren wir: _Thank you for your enquiry. We offer:_]
#v(6pt)
#positions((
  ("Isolierglas 3-fach, 2.4 × 1.2 m / Triple glazing", if v2 { 6 } else { 4 }, 1180),
  ("Aluminiumrahmen eloxiert / Anodised frames", if v2 { 6 } else { 4 }, 640),
  ("Montage vor Ort (Stunden) / On-site fitting (hours)", 18, 115),
  ..if v2 { (("Entsorgung Altglas / Disposal of old glass", 1, 380),) } else { () },
))
#v(10pt)
Lieferfrist 4 Wochen nach Auftragsbestätigung. _Delivery 4 weeks after confirmation._
#v(1fr)
Freundliche Grüsse / Kind regards \ M. Gerber, Alpenglas AG

// "Seeblick catering invoice" in TWO versions (the corrected one replaces a wrong head count):
// build.sh compiles --input rev=1 and rev=2. Filed in Business Years / 2026 / 03 with annotations.
#import "style.typ": *
#let v2 = sys.inputs.at("rev", default: "1") == "2"
#let seeblick = (name: "Seeblick Catering GmbH", address: "Quai 9 · 6006 Luzern", accent: rgb("#15803d"))
#show: sample.with(title: "Rechnung Personalanlass")
#letterhead(seeblick, [Catering & Events], [#if v2 [KORRIGIERT / CORRECTED] else []])
#addressed([Kristall Holding AG \ Personal / HR \ Seestrasse 4 \ 6004 Luzern], [*Rechnung / Invoice*], [SC-2026-118], [*Anlass / Event*], [Frühlingsapéro, 19.03.2026])
#v(14pt)
#heading-pair("Rechnung Personalanlass", "Invoice for the staff event")
#positions((
  ("Apéro riche pro Person / per person", if v2 { 42 } else { 48 }, 38),
  ("Getränkepauschale / Drinks package", if v2 { 42 } else { 48 }, 22),
  ("Servicepersonal (Stunden) / Staff (hours)", 12, 55),
))
#if v2 [#v(8pt) Korrektur: 42 statt 48 Gäste gemäss Ihrer Meldung. _Corrected to 42 guests, as you reported._]

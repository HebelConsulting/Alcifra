// "Alpenglas invoice AG-2026-031": the Manual tenant's showcase document (annotations, the review task, tags,
// the chat thread). Built by build.sh.
#import "style.typ": *
#let alpenglas = (name: "Alpenglas AG", address: "Industriestrasse 12 · 6010 Kriens", accent: rgb("#1f6f8b"))
#show: sample.with(title: "Rechnung AG-2026-031")
#letterhead(alpenglas, [Glas- und Fassadenbau / Glass and façades], [MWST CHE-123.456.789])

#addressed([Kristall Holding AG \ Buchhaltung / Accounting \ Seestrasse 4 \ 6004 Luzern], [*Rechnung / Invoice*], [AG-2026-031], [*Datum / Date*], [12.03.2026], [*Fällig / Due*], [11.04.2026],
    [*Auftrag / Order*], [Offerte 2026-022])
#v(14pt)
#heading-pair("Rechnung", "Invoice")
Für die Erneuerung der Eingangsfassade am Standort Seestrasse 4 stellen wir Ihnen in Rechnung:
_For the renewal of the entrance façade at Seestrasse 4 we invoice:_
#v(6pt)
#positions((
  ("Isolierglas 3-fach, 2.4 × 1.2 m / Triple glazing", 6, 1180),
  ("Aluminiumrahmen eloxiert / Anodised frames", 6, 640),
  ("Montage vor Ort (Stunden) / On-site fitting (hours)", 18, 115),
  ("Entsorgung Altglas / Disposal of old glass", 1, 380),
))
#v(10pt)
Zahlbar innert 30 Tagen auf IBAN CH93 0000 0000 0000 0000 1. _Payable within 30 days._
#v(1fr)
Freundliche Grüsse / Kind regards \ Alpenglas AG

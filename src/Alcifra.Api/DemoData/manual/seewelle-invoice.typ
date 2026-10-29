// Seewelle's monthly invoice; build.sh compiles one per month with --input month=1..3.
#import "style.typ": *
#let m = int(sys.inputs.at("month", default: "1"))
#let months = ("Januar / January", "Februar / February", "März / March")
#let seewelle = (name: "Seewelle Telecom AG", address: "Bahnhofplatz 1 · 6002 Luzern", accent: rgb("#c2410c"))
#show: sample.with(title: "Seewelle Rechnung 2026-0" + str(m))
#letterhead(seewelle, [Business Internet & Telefonie], [Kundennummer / Customer 880-4412])
#addressed([Kristall Holding AG \ Seestrasse 4 \ 6004 Luzern], [*Rechnung / Invoice*], [SW-26-0#(m)-4412], [*Periode / Period*], [#months.at(m - 1) 2026],
    [*Datum / Date*], [01.0#(m).2026])
#v(14pt)
#heading-pair("Monatsrechnung", "Monthly invoice")
#positions((
  ("Glasfaser 10 Gbit/s / Fibre", 1, 590),
  ("Telefonlinien / Phone lines", 20, 18.5),
  ("Mobile Abos / Mobile plans", 12, 23.33),
  ("Roaming", 1, (42.10, 0, 118.40).at(m - 1)),
))

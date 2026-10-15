// "Seewelle Telecom service agreement": carries the Manual tenant's external link. Built by build.sh.
#import "style.typ": *
#let seewelle = (name: "Seewelle Telecom AG", address: "Bahnhofplatz 1 · 6002 Luzern", accent: rgb("#c2410c"))
#show: sample.with(title: "Dienstleistungsvertrag Seewelle Business")
#letterhead(seewelle, [Business Internet & Telefonie], [Kundennummer / Customer 880-4412])
#heading-pair("Dienstleistungsvertrag Seewelle Business", "Service agreement Seewelle Business")
#facts([*Kundin / Customer*], [Kristall Holding AG, Seestrasse 4, 6004 Luzern],
  [*Produkt / Product*], [Glasfaser 10 Gbit/s, 20 Telefonlinien, Mobile für 12 Mitarbeitende],
  [*Beginn / Start*], [1. Januar 2026], [*Mindestlaufzeit / Minimum term*], [24 Monate / months],
  [*Monatlich / Monthly*], [CHF 1'240.00 exkl. MWST])
#v(12pt)
Die Allgemeinen Geschäftsbedingungen Ausgabe 2026 sind Bestandteil dieses Vertrags. Störungen meldet die Kundin rund
um die Uhr an die Business-Hotline. _The 2026 general terms are part of this agreement; faults are reported to the
business hotline around the clock._
#signatures([Kristall Holding AG], [Seewelle Telecom AG])

// An outdated price list the Manual tenant keeps in its recycle bin.
#import "style.typ": *
#show: sample.with(title: "Preisliste 2025")
#letterhead(kristall, [Dienstleistungen / Services], [Gültig 2025 / Valid 2025])
#heading-pair("Preisliste 2025", "Price list 2025")
#table(columns: (1fr, auto), stroke: 0.4pt, [*Leistung / Service*], [*CHF*],
  [Beratung pro Stunde / Consulting per hour], [180], [Projektpauschale / Project flat fee], [4'500])

// Shared look of the Manual tenant's samples (Kristall Holding AG, ManualDemoSeeder). Every sample imports this,
// so the set reads as one company's archive; each issuer keeps its own accent so a supplier's invoice is told
// apart from Kristall's own papers at a glance. Fictional companies and people throughout. Built by build.sh.

#let kristall = (name: "Kristall Holding AG", address: "Seestrasse 4 · 6004 Luzern", accent: rgb("#5b2a86"))

#let sample(title: "", lang: "de", body) = {
  set document(title: title)
  set page(paper: "a4", margin: (x: 2.2cm, y: 2cm), numbering: "1 / 1",
    footer: context align(center, text(size: 8pt, fill: luma(120))[
      Fiktives Demo-Dokument von SimplArchive. / A fictional SimplArchive demo document. · #counter(page).display("1 / 1", both: true)
    ]))
  set text(font: "Libertinus Serif", size: 10.5pt, lang: lang)
  body
}

#let letterhead(issuer, subtitle, note) = {
  block(fill: issuer.accent, inset: (x: 16pt, y: 14pt), radius: 4pt, width: 100%)[
    #set text(fill: white)
    #grid(columns: (1fr, auto), align: (left + horizon, right + horizon),
      [#text(size: 19pt, weight: "bold")[#issuer.name] \ #text(size: 9pt)[#subtitle]],
      [#text(size: 9pt)[#issuer.address \ #note]],
    )
  ]
  v(14pt)
}

#let heading-pair(de, en) = {
  text(size: 16pt, weight: "bold")[#de]
  v(-6pt)
  text(size: 11pt, fill: luma(90))[#en]
  v(8pt)
}

#let facts(..rows) = grid(columns: (auto, auto), column-gutter: 14pt, row-gutter: 6pt, ..rows)

// The recipient's address beside the document's facts, each in its own half so neither squeezes the other.
#let addressed(address, ..rows) = grid(columns: (1fr, auto), column-gutter: 20pt, address, facts(..rows))

// Swiss money: two decimals and an apostrophe for thousands ("CHF 14'452.97").
#let chf(x) = {
  let cents = int(calc.round(x * 100))
  let whole = str(calc.quo(cents, 100))
  let frac = str(calc.rem(cents, 100))
  let grouped = whole.clusters().rev().chunks(3).map(c => c.rev().join()).rev().join("'")
  "CHF " + grouped + "." + (if frac.len() == 1 { "0" + frac } else { frac })
}

// An invoice's position table; rows are (description, quantity, unit price) and the totals follow from them.
#let positions(rows, vat: 8.1) = {
  let net = rows.map(r => r.at(1) * r.at(2)).sum()
  let tax = calc.round(net * vat / 100, digits: 2)
  table(columns: (1fr, auto, auto, auto), stroke: none, inset: (x: 4pt, y: 5pt),
    table.hline(stroke: 0.6pt),
    [*Position / Item*], [*Menge / Qty*], [*Preis / Price*], [*Betrag / Amount*],
    table.hline(stroke: 0.4pt),
    ..rows.map(r => (r.at(0), str(r.at(1)), chf(r.at(2)), chf(r.at(1) * r.at(2)))).flatten(),
    table.hline(stroke: 0.4pt),
    [], [], [Netto / Net], chf(net),
    [], [], [MWST / VAT #vat %], chf(tax),
    [], [], [*Total*], [*#chf(net + tax)*],
    table.hline(stroke: 0.6pt),
  )
}

#let signatures(left, right) = {
  v(1fr)
  grid(columns: (1fr, 1fr), column-gutter: 30pt,
    [#line(length: 100%, stroke: 0.5pt) #left],
    [#line(length: 100%, stroke: 0.5pt) #right],
  )
}

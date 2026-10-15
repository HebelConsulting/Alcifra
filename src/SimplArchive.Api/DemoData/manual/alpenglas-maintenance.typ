// "Alpenglas maintenance agreement WV-2026-07": the Manual tenant's MULTI-PAGE sample (page navigation,
// thumbnails, search hits on a later page). Built by build.sh.
#import "style.typ": *
#let alpenglas = (name: "Alpenglas AG", address: "Industriestrasse 12 · 6010 Kriens", accent: rgb("#1f6f8b"))
#show: sample.with(title: "Wartungsvertrag WV-2026-07")
#letterhead(alpenglas, [Glas- und Fassadenbau / Glass and façades], [Vertrag / Agreement WV-2026-07])
#heading-pair("Wartungsvertrag Fassaden und Glastüren", "Maintenance agreement for façades and glass doors")
#facts([*Auftraggeberin / Client*], [Kristall Holding AG, Seestrasse 4, 6004 Luzern],
  [*Auftragnehmerin / Contractor*], [Alpenglas AG, Industriestrasse 12, 6010 Kriens],
  [*Beginn / Start*], [1. April 2026], [*Laufzeit / Term*], [3 Jahre, danach jährlich verlängerbar / 3 years, then yearly])
#let clause(n, de, en, body) = { v(10pt); text(weight: "bold")[§ #n #de]; text(fill: luma(90))[ \/ #en]; v(-4pt); body }
#clause(1, "Gegenstand", "Subject")[Die Auftragnehmerin wartet sämtliche Glasfassaden, automatischen Schiebetüren und
  Brandschutzverglasungen am Standort Seestrasse 4. _The contractor maintains all glass façades, automatic sliding
  doors and fire-rated glazing at Seestrasse 4._]
#clause(2, "Leistungen", "Services")[Zweimal jährlich Inspektion und Reinigung der Dichtungen; jährliche Prüfung der
  Türantriebe nach Herstellervorgabe; Protokoll jeder Wartung an die Auftraggeberin. _Inspection twice a year,
  yearly drive check, a written report after every visit._]
#pagebreak()
#clause(3, "Reaktionszeit", "Response time")[Bei Störungen an Fluchttüren innert 4 Stunden, übrige Störungen innert
  2 Arbeitstagen. _Emergency exits within 4 hours, everything else within 2 working days._]
#clause(4, "Vergütung", "Fees")[Pauschal CHF 4'800 pro Jahr exkl. MWST, zahlbar quartalsweise im Voraus. Material und
  Ersatzteile werden nach Aufwand verrechnet. _A flat CHF 4,800 a year excl. VAT, quarterly in advance; parts at cost._]
#clause(5, "Haftung", "Liability")[Die Haftung richtet sich nach Art. 97 ff. OR; ausgeschlossen sind Folgeschäden
  ausser bei Vorsatz oder grober Fahrlässigkeit. _Statutory liability; consequential loss excluded except for intent
  or gross negligence._]
#pagebreak()
#clause(6, "Kündigung", "Termination")[Nach Ablauf der festen Laufzeit mit einer Frist von 6 Monaten auf Ende eines
  Vertragsjahres. _After the fixed term, on 6 months' notice to the end of a contract year._]
#clause(7, "Gerichtsstand", "Jurisdiction")[Luzern. Es gilt Schweizer Recht. _Lucerne; Swiss law applies._]
#signatures([Kristall Holding AG], [Alpenglas AG])

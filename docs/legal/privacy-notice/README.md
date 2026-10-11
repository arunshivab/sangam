# Privacy notice (draft for counsel)

*rc.8, founder decision 8 (11 October 2026): Claude drafts the privacy notice in English, Hindi and Malayalam; counsel
approves it before go-live.*

| File | Language | Status |
|---|---|---|
| [privacy-notice.en-IN.txt](privacy-notice.en-IN.txt) | English | Draft 0.1 for counsel |
| [privacy-notice.hi-IN.txt](privacy-notice.hi-IN.txt) | Hindi | Draft 0.1; translation of the English draft, to be checked by a native speaker after counsel approves the English |
| [privacy-notice.ml-IN.txt](privacy-notice.ml-IN.txt) | Malayalam | Draft 0.1; as Hindi |

The files are plain text, one paragraph per block separated by a blank line, which is the format `/privacy` reads
(`Sangam:Legal:PrivacyPath`, `LegalDocuments.Read`). The first paragraph of each is the draft notice and is removed
when approved. `/privacy` shows the pending notice until a path is configured.

## For counsel

Placeholders are marked `[COUNSEL: …]`. In particular:

1. The data fiduciary: the founder today; the company that will own Sangam when it is formed (SGM-910 section 2).
2. The grievance officer's name and postal address, and the acknowledgement and answer periods
   (`Sangam:Grievance:*` settings show them on the grievance page).
3. Retention: the seven-year encrypted audit archive (to defend legal claims and investigate incidents) and one-year
   security logs (SGM-910 sections 6 and 9).
4. Whether consent or "legitimate use" (DPDP Act section 7) is the right ground for each purpose in section 3.
5. Whether the Hindi and Malayalam texts, once approved, are equally authoritative or translations of the English.
6. The DPDP Rules' notice requirements as finally notified (Claude's knowledge ends mid-2026).

Every fact in the notice is drawn from SGM-910 and the code as of 1.0.0-rc.9; when a fact changes, change the notice.

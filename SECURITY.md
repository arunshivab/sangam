# Security policy

Sangam is an identity provider. A vulnerability in Sangam is a vulnerability in every partner
application that trusts it, so we treat every report seriously and respond quickly.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security problems.**

Email **security@sangamid.in** with:

- a description of the issue and the component affected
  (`id.sangamid.in`, `account.sangamid.in`, `admin.sangamid.in`, `Sangam.Client`, or the code),
- steps to reproduce, or a proof of concept,
- the impact as you understand it,
- how you would like to be credited, if at all.

You will receive an acknowledgement within **3 working days** and a substantive response
(assessment, planned fix, timeline) within **10 working days**. We will keep you informed as
the fix progresses and tell you when it ships.

## Scope

In scope: the code in this repository, the hosted services at `*.sangamid.in`, and the
`Sangam.Client` package. Out of scope: partner applications that integrate with Sangam
(report those to the partner), denial of service by traffic volume, and findings that require
physical access to a user's device.

## Safe harbour

Good-faith research that respects users' privacy, avoids data destruction and service
disruption, and does not access data beyond what is needed to demonstrate the issue will not
be met with legal action by imagiQa Healthcare Services Pvt Ltd. Do not access, modify or
retain personal data belonging to other users; if you encounter such data, stop and report.

## Supported versions

Sangam is pre-release (v0.x). Security fixes are applied to `main` and released as the next
tag; there are no long-term support branches yet.

## Data protection

imagiQa Healthcare Services Pvt Ltd is the data fiduciary for Sangam under India's Digital
Personal Data Protection Act, 2023. Privacy enquiries that are not security vulnerabilities go
to **privacy@sangamid.in**.

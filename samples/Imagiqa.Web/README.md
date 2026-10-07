# imagiQa — a sample hospital application on Sangam

A deliberately small hospital information system that shows how an application uses Sangam:
people sign in with Sangam, and what they may do comes from the roles their hospital gives them on
Sangam's partner console. It references only `Sangam.Client`. See ADR-0007.

## Run it

Needs PostgreSQL with a database `imagiqa_sample` owned by `sangam_identity`, and Sangam's identity
server running on port 5100 in Development (it registers imagiQa and its roles).

```powershell
dotnet run --project src/Sangam.Identity.Server      # 5100
dotnet run --project src/Sangam.Partner.Web          # 5400
dotnet run --project samples/Imagiqa.Web             # 5500 — creates its own tables on first start
```

## Walk through it

1. Open `http://localhost:5500` and sign in (register first if you need to). You are told you have
   no role at a hospital yet — and imagiQa is now linked to your account.
2. On the operator console (5300), **Assign owner** on *imagiQa* to an account with an
   authenticator app.
3. As that owner, on the partner console (5400): open imagiQa, add a hospital, find people who have
   signed in to imagiQa, and give them *doctor* or *nurse*.
4. Sign out of imagiQa and sign in again. Nurses register patients and record vital signs; doctors
   register patients and write notes. Patients are seen only at their own hospital.

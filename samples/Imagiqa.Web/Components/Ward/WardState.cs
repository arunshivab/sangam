using Imagiqa.Web.Records;
using Sangam.Client;

namespace Imagiqa.Web.Components.Ward;

/// <summary>
/// Who is working, and in which hospital, for one browser session. The hospitals offered are
/// exactly those where Sangam says the person is a doctor or a nurse.
/// </summary>
public sealed class WardState
{
    /// <summary>Raised when the person switches hospital.</summary>
    public event Action? Changed;

    /// <summary>The person, as Sangam signed them in.</summary>
    public SangamUser? User { get; private set; }

    /// <summary>Hospitals where they are a doctor or a nurse.</summary>
    public IReadOnlyList<SangamMembership> Hospitals { get; private set; } = [];

    /// <summary>The hospital they are working in now.</summary>
    public SangamMembership? Current { get; private set; }

    /// <summary>Whether they are a doctor in the current hospital.</summary>
    public bool IsDoctor => User is not null && Current is not null && PatientRecords.Holds(User, Current.OrganisationId, ImagiqaRoles.Doctor);

    /// <summary>Whether they are a nurse in the current hospital.</summary>
    public bool IsNurse => User is not null && Current is not null && PatientRecords.Holds(User, Current.OrganisationId, ImagiqaRoles.Nurse);

    /// <summary>Takes the person from the sign-in; keeps the current hospital if it is still theirs.</summary>
    /// <param name="user">The signed-in person, or <see langword="null"/>.</param>
    public void Load(SangamUser? user)
    {
        User = user;
        Hospitals = user is null ? [] : [.. user.Organisations.Where(o => PatientRecords.IsClinician(user, o.OrganisationId))];
        Current = Hospitals.FirstOrDefault(h => h.OrganisationId == Current?.OrganisationId) ?? (Hospitals.Count > 0 ? Hospitals[0] : null);
    }

    /// <summary>Switches to another of their hospitals.</summary>
    /// <param name="hospitalId">The hospital.</param>
    public void Switch(Guid hospitalId)
    {
        SangamMembership? next = Hospitals.FirstOrDefault(h => h.OrganisationId == hospitalId);
        if (next is not null && next.OrganisationId != Current?.OrganisationId)
        {
            Current = next;
            Changed?.Invoke();
        }
    }
}

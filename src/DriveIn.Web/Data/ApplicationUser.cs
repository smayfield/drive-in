using Microsoft.AspNetCore.Identity;

namespace DriveIn.Web.Data;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }

    // Set for employee accounts: the one theater this account is valid for.
    public int? EmployeeTheaterId { get; set; }
    public Theater? EmployeeTheater { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Theater> OwnedTheaters { get; set; } = [];

    // Employee accounts only: roles within EmployeeTheater.
    public List<EmployeeRole> EmployeeRoles { get; set; } = [];
}

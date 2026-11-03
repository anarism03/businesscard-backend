using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessCard.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestrictUserRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing rows are never deleted or rewritten. The constraints are added NOT VALID, so every new
            // INSERT/UPDATE is checked immediately; old rows are validated only when they already comply.
            // Rows with an undefined role / role-company mismatch stay untouched (the API refuses those accounts)
            // and are reported with a NOTICE so an administrator can fix them and run VALIDATE CONSTRAINT later.
            migrationBuilder.Sql("""
                ALTER TABLE "Users" ADD CONSTRAINT "CK_Users_Role"
                    CHECK ("Role" IN (0, 1, 2)) NOT VALID;
                ALTER TABLE "Users" ADD CONSTRAINT "CK_Users_RoleCompany"
                    CHECK (("Role" = 0 AND "CompanyId" IS NULL) OR ("Role" IN (1, 2) AND "CompanyId" IS NOT NULL)) NOT VALID;
                DO $$
                DECLARE bad_roles integer; bad_companies integer;
                BEGIN
                    SELECT count(*) INTO bad_roles FROM "Users" WHERE "Role" NOT IN (0, 1, 2);
                    SELECT count(*) INTO bad_companies FROM "Users"
                        WHERE NOT (("Role" = 0 AND "CompanyId" IS NULL) OR ("Role" IN (1, 2) AND "CompanyId" IS NOT NULL));
                    IF bad_roles = 0 THEN
                        ALTER TABLE "Users" VALIDATE CONSTRAINT "CK_Users_Role";
                    ELSE
                        RAISE NOTICE 'CK_Users_Role left NOT VALID: % user(s) have an undefined role', bad_roles;
                    END IF;
                    IF bad_companies = 0 THEN
                        ALTER TABLE "Users" VALIDATE CONSTRAINT "CK_Users_RoleCompany";
                    ELSE
                        RAISE NOTICE 'CK_Users_RoleCompany left NOT VALID: % user(s) have a role/company mismatch', bad_companies;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_Role",
                table: "Users");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Users_RoleCompany",
                table: "Users");
        }
    }
}

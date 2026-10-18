using Microsoft.EntityFrameworkCore.Migrations;

namespace FinanceForgeLab.Infrastructure.Migrations;

public partial class RecurringExpenses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("RecurringExpenses",table=>new{
            Id=table.Column<long>("INTEGER",nullable:false).Annotation("Sqlite:Autoincrement",true),
            HouseholdId=table.Column<long>("INTEGER",nullable:false),
            Name=table.Column<string>("TEXT",nullable:false),
            CategoryId=table.Column<long>("INTEGER",nullable:true),
            AccountId=table.Column<long>("INTEGER",nullable:true),
            ContainsText=table.Column<string>("TEXT",nullable:false),
            MatchKey=table.Column<string>("TEXT",nullable:false),
            Frequency=table.Column<string>("TEXT",nullable:false),
            Included=table.Column<bool>("INTEGER",nullable:false),
            Version=table.Column<int>("INTEGER",nullable:false)
        },constraints:table=>{
            table.PrimaryKey("PK_RecurringExpenses",x=>x.Id);
            table.ForeignKey("FK_RecurringExpenses_Households_HouseholdId",x=>x.HouseholdId,"Households","Id",onDelete:ReferentialAction.Restrict);
            table.ForeignKey("FK_RecurringExpenses_Categories_CategoryId",x=>x.CategoryId,"Categories","Id",onDelete:ReferentialAction.Restrict);
            table.ForeignKey("FK_RecurringExpenses_Accounts_AccountId",x=>x.AccountId,"Accounts","Id",onDelete:ReferentialAction.Restrict);
        });
        migrationBuilder.CreateIndex("IX_RecurringExpenses_AccountId","RecurringExpenses","AccountId");
        migrationBuilder.CreateIndex("IX_RecurringExpenses_CategoryId","RecurringExpenses","CategoryId");
        migrationBuilder.CreateIndex("IX_RecurringExpenses_HouseholdId_MatchKey","RecurringExpenses",new[]{"HouseholdId","MatchKey"},unique:true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)=>migrationBuilder.DropTable("RecurringExpenses");
}

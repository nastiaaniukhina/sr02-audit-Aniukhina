using System.Globalization;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LibraryDesk.Tests;

public class FixedClockManager : LoanManager
{
    private readonly DateTime _now;

    public FixedClockManager(LibraryStore store, DateTime now) : base(store)
    {
        _now = now;
    }

    protected override DateTime Now() => _now;
}

public abstract class TestBase
{
    protected static readonly DateTime Noon = DateTime.Today.AddHours(12);

    protected TestBase()
    {
        LibraryStore.NextLoanId = 1001;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    protected static LibraryStore NewStore()
    {
        var s = new LibraryStore();
        s.AddReader("R-1", "Adult Reader", "", "adult", 0, false);
        s.AddReader("R-2", "Student Reader", "", "student", 0, false);
        s.AddReader("R-3", "Debt Exactly 50", "", "adult", 50, false);
        s.AddReader("R-4", "Debt 51", "", "adult", 51, false);
        s.AddBook("INV-1", "Fiction", "A", "fiction", false, 100);
        s.AddBook("INV-2", "Magazine", "-", "periodical", false, 60);
        s.AddBook("INV-3", "Reference", "B", "reference", false, 900);
        s.AddBook("INV-4", "Rare", "C", "fiction", true, 1000);
        s.AddBook("INV-5", "Old book", "D", "fiction", false, 150);
        return s;
    }

    protected static Loan AddLoan(LibraryStore s, string card, string inv, int daysOverdue)
    {
        var loan = new Loan
        {
            Id = LibraryStore.NextLoanId++,
            ReaderCard = card,
            InventoryNo = inv,
            IssuedOn = DateTime.Today.AddDays(-14 - daysOverdue),
            DueDate = DateTime.Today.AddDays(-daysOverdue)
        };
        s.Loans.Add(loan);
        s.FindBook(inv).Available = false;
        return loan;
    }

    protected static FixedClockManager Mgr(LibraryStore s) => new FixedClockManager(s, Noon);
}

// ---------- Розрахунковий вузол: строк видачі й пеня (Issue) ----------
public class IssueTests : TestBase
{
    [Theory]
    [InlineData("R-1", "INV-1", 0, 14)]
    [InlineData("R-2", "INV-1", 0, 30)]
    [InlineData("R-1", "INV-2", 0, 7)]
    [InlineData("R-2", "INV-2", 0, 7)]
    [InlineData("R-1", "INV-1", 10, 10)]
    [InlineData("R-1", "INV-1", 20, 14)]
    [InlineData("R-1", "INV-1", -5, 14)]
    public void Issue_ReaderBookDays_ReturnsCapturedTerm(string card, string inv, int days, int expected)
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue(card, inv, days, false);

        Assert.Equal(1001, id);
        Assert.Equal(expected, mgr.LastTerm);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(3, 0.0)]
    [InlineData(4, 2.5)]
    [InlineData(10, 17.5)]
    [InlineData(23, 50.0)]
    [InlineData(24, 52.5)]
    [InlineData(70, 150.0)]
    [InlineData(80, 150.0)]
    public void Issue_OverdueDays_ReturnsCapturedFine(int daysOverdue, double expectedFine)
    {
        var store = NewStore();
        AddLoan(store, "R-1", "INV-5", daysOverdue);
        var mgr = Mgr(store);

        mgr.Issue("R-1", "INV-1", 0, false);

        Assert.Equal((decimal)expectedFine, mgr.LastFine);
    }

    [Fact]
    public void Issue_ReaderWithFine_ShortTerm7()
    {
        var store = NewStore();
        AddLoan(store, "R-1", "INV-5", 4);
        var mgr = Mgr(store);

        var id = mgr.Issue("R-1", "INV-1", 0, false);

        Assert.True(id > 0);
        Assert.Equal(7, mgr.LastTerm);
    }

    [Fact]
    public void Issue_FinePlusDebtOver50_DeniedWithMessage()
    {
        var store = NewStore();
        AddLoan(store, "R-1", "INV-5", 24);
        var mgr = Mgr(store);

        var id = mgr.Issue("R-1", "INV-1", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Debt limit exceeded: 52.50", mgr.LastError);
    }

    [Fact]
    public void Issue_DebtExactly50_Allowed()
    {
        var mgr = Mgr(NewStore());

        Assert.True(mgr.Issue("R-3", "INV-1", 0, false) > 0);
    }

    [Fact]
    public void Issue_Debt51_Denied()
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue("R-4", "INV-1", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Debt limit exceeded: 51.00", mgr.LastError);
    }

    [Fact]
    public void Issue_ReferenceBookWithoutReadingRoom_Denied()
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue("R-1", "INV-3", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Book INV-3 is for the reading room only", mgr.LastError);
    }

    [Fact]
    public void Issue_RareBookInReadingRoomAtNoon_TermZero()
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue("R-1", "INV-4", 0, true);

        Assert.Equal(1001, id);
        Assert.Equal(0, mgr.LastTerm);
    }

    [Fact]
    public void Issue_ReadingRoomAt19_Denied()
    {
        var mgr = new FixedClockManager(NewStore(), DateTime.Today.AddHours(19));

        var id = mgr.Issue("R-1", "INV-4", 0, true);

        Assert.Equal(-1, id);
        Assert.Equal("Reading room closes at 20:00, no issues after 19:00", mgr.LastError);
    }

    [Fact]
    public void Issue_ReadingRoomAt1859_Allowed()
    {
        var mgr = new FixedClockManager(NewStore(), DateTime.Today.AddHours(18).AddMinutes(59));

        Assert.True(mgr.Issue("R-1", "INV-4", 0, true) > 0);
    }

    // ----- сценарії помилок -----
    [Fact]
    public void Issue_NullCard_DeniedWithMessage()
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue(null, "INV-1", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Card number is empty", mgr.LastError);
    }

    [Fact]
    public void Issue_UnknownReader_DeniedWithMessage()
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue("R-9999", "INV-1", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Reader not found: R-9999", mgr.LastError);
    }

    [Fact]
    public void Issue_CardLowercaseWithSpaces_Found()
    {
        var mgr = Mgr(NewStore());

        Assert.True(mgr.Issue("r-1 ", "INV-1", 0, false) > 0);
    }

    [Fact]
    public void Issue_BookAlreadyIssued_DeniedWithMessage()
    {
        var mgr = Mgr(NewStore());
        mgr.Issue("R-1", "INV-1", 0, false);

        var id = mgr.Issue("R-2", "INV-1", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Book is not available: INV-1", mgr.LastError);
    }

    [Fact]
    public void Issue_UnknownBook_DeniedWithMessage()
    {
        var mgr = Mgr(NewStore());

        var id = mgr.Issue("R-1", "INV-404", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Book is not available: INV-404", mgr.LastError);
    }

    [Fact]
    public void Issue_SixthBookForAdult_LoanLimitReached()
    {
        var store = NewStore();
        for (int i = 0; i < 5; i++)
            store.AddBook("INV-X" + i, "Extra " + i, "E", "fiction", false, 10);
        var mgr = Mgr(store);
        for (int i = 0; i < 5; i++)
            Assert.True(mgr.Issue("R-1", "INV-X" + i, 0, false) > 0);

        var id = mgr.Issue("R-1", "INV-1", 0, false);

        Assert.Equal(-1, id);
        Assert.Equal("Loan limit reached: 5 of 5", mgr.LastError);
    }

    // Характеризаційний тест. Лічильник відмов збільшується наперед і
    // зменшується при успіху. Це зафіксовано як наявна поведінка.
    [Fact]
    public void Issue_Success_CountersIssued1Denied0()
    {
        var mgr = Mgr(NewStore());

        mgr.Issue("R-1", "INV-1", 0, false);

        Assert.Equal(1, mgr.IssuedToday);
        Assert.Equal(0, mgr.DeniedToday);
    }

    [Fact]
    public void Issue_Failure_CountersIssued0Denied1()
    {
        var mgr = Mgr(NewStore());

        mgr.Issue(null, "INV-1", 0, false);

        Assert.Equal(0, mgr.IssuedToday);
        Assert.Equal(1, mgr.DeniedToday);
    }
}

// ---------- Зміна стану: продовження ----------
public class ExtendTests : TestBase
{
    [Fact]
    public void Extend_FreshLoan_AddsDaysAndSetsExtended()
    {
        var store = NewStore();
        var mgr = Mgr(store);
        var id = mgr.Issue("R-1", "INV-1", 0, false);
        var loan = store.FindLoan(id);
        var due = loan.DueDate;

        Assert.True(mgr.Extend(id, 14));

        Assert.Equal(due.AddDays(14), loan.DueDate);
        Assert.Equal(1, loan.Extensions);
        Assert.Equal(LoanStatus.Extended, loan.Status);
    }

    [Theory]
    [InlineData(0, 14)]
    [InlineData(31, 14)]
    [InlineData(30, 30)]
    [InlineData(7, 7)]
    public void Extend_RequestedDays_UsesCapturedDays(int requested, int expected)
    {
        var store = NewStore();
        var mgr = Mgr(store);
        var id = mgr.Issue("R-1", "INV-1", 0, false);
        var due = store.FindLoan(id).DueDate;

        mgr.Extend(id, requested);

        Assert.Equal(due.AddDays(expected), store.FindLoan(id).DueDate);
    }

    [Fact]
    public void Extend_Periodical_Refused()
    {
        var mgr = Mgr(NewStore());
        var id = mgr.Issue("R-1", "INV-2", 0, false);

        Assert.False(mgr.Extend(id, 14));
        Assert.Equal("Item INV-2 cannot be extended", mgr.LastError);
    }

    [Fact]
    public void Extend_OverdueLoan_Refused()
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", "INV-5", 5);
        var mgr = Mgr(store);

        Assert.False(mgr.Extend(loan.Id, 14));
        Assert.Equal("Loan #1001 is overdue, pay the fine first", mgr.LastError);
    }

    [Fact]
    public void Extend_UnknownLoan_Refused()
    {
        var mgr = Mgr(NewStore());

        Assert.False(mgr.Extend(9999, 14));
        Assert.Equal("Loan not found: 9999", mgr.LastError);
    }

    [Fact]
    public void Extend_ReturnedLoan_Refused()
    {
        var store = NewStore();
        var mgr = Mgr(store);
        var id = mgr.Issue("R-1", "INV-1", 0, false);
        new ReturnDesk(store).Return(id);

        Assert.False(mgr.Extend(id, 14));
        Assert.Equal("Loan #1001 is closed (Returned)", mgr.LastError);
    }

    // Характеризаційний тест. Правила кажуть "не більше 2 продовжень",
    // але перевірка Extensions > MAX_EXT пропускає третє. Це зафіксовано
    // як наявна поведінка; див. TD-01 у реєстрі боргу.
    [Fact]
    public void Extend_ThirdTimeAllowed_FourthRefused()
    {
        var store = NewStore();
        var mgr = Mgr(store);
        var id = mgr.Issue("R-1", "INV-1", 0, false);

        Assert.True(mgr.Extend(id, 14));
        Assert.True(mgr.Extend(id, 14));
        Assert.True(mgr.Extend(id, 14));
        Assert.False(mgr.Extend(id, 14));
        Assert.Equal("Extension limit reached for loan #1001", mgr.LastError);
    }

    // Характеризаційний тест. Видача з терміном "сьогодні" вважається
    // простроченою одразу, бо порівнюється Now з північчю. Див. TD-02.
    [Fact]
    public void IsOverdue_DueToday_ReturnsTrue()
    {
        var mgr = Mgr(NewStore());
        var loan = new Loan { DueDate = DateTime.Today };

        Assert.True(mgr.IsOverdue(loan));
    }

    [Fact]
    public void IsOverdue_DueTomorrow_ReturnsFalse()
    {
        var mgr = Mgr(NewStore());
        var loan = new Loan { DueDate = DateTime.Today.AddDays(1) };

        Assert.False(mgr.IsOverdue(loan));
    }

    [Theory]
    [InlineData("adult", false, 5)]
    [InlineData("pensioner", false, 5)]
    [InlineData("staff", false, 10)]
    [InlineData("child", false, 3)]
    [InlineData("staff", true, 0)]
    public void LoanLimit_CategoryBlocked_ReturnsCapturedLimit(string category, bool blocked, int expected)
    {
        var mgr = Mgr(NewStore());
        var reader = new Reader { Category = category, Blocked = blocked };

        Assert.Equal(expected, mgr.LoanLimit(reader));
    }
}

// ---------- Повернення й втрати (ReturnDesk) ----------
public class ReturnDeskTests : TestBase
{
    [Fact]
    public void Return_OnTime_FineZeroAndBookAvailable()
    {
        var store = NewStore();
        var id = Mgr(store).Issue("R-1", "INV-1", 0, false);

        var fine = new ReturnDesk(store).Return(id);

        Assert.Equal(0m, fine);
        Assert.Equal(LoanStatus.Returned, store.FindLoan(id).Status);
        Assert.True(store.FindBook("INV-1").Available);
    }

    [Theory]
    [InlineData(3, 0.0)]
    [InlineData(4, 2.5)]
    [InlineData(10, 17.5)]
    [InlineData(70, 150.0)]
    public void Return_OverdueDays_ReturnsCapturedFine(int daysOverdue, double expected)
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", "INV-1", daysOverdue);

        var fine = new ReturnDesk(store).Return(loan.Id);

        Assert.Equal((decimal)expected, fine);
        Assert.Equal((decimal)expected, store.FindReader("R-1").Debt);
    }

    [Fact]
    public void Return_FineExactly50_DoesNotBlockReader()
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", "INV-1", 23);

        new ReturnDesk(store).Return(loan.Id);

        Assert.False(store.FindReader("R-1").Blocked);
    }

    [Fact]
    public void Return_FineAbove50_BlocksReader()
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", "INV-1", 24);

        new ReturnDesk(store).Return(loan.Id);

        Assert.True(store.FindReader("R-1").Blocked);
    }

    [Fact]
    public void Return_Twice_ThrowsInvalidOperation()
    {
        var store = NewStore();
        var id = Mgr(store).Issue("R-1", "INV-1", 0, false);
        var desk = new ReturnDesk(store);
        desk.Return(id);

        var ex = Assert.Throws<InvalidOperationException>(() => desk.Return(id));

        Assert.Contains("already returned", ex.Message);
    }

    [Fact]
    public void Return_UnknownLoan_ReturnsMinus1()
    {
        var desk = new ReturnDesk(NewStore());

        Assert.Equal(-1m, desk.Return(9999));
    }

    [Fact]
    public void Return_AfterLost_NoSecondCharge()
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", "INV-1", 30);
        var desk = new ReturnDesk(store);
        desk.MarkLost(loan.Id);

        var fine = desk.Return(loan.Id);

        Assert.Equal(0m, fine);
        Assert.Equal(300m, store.FindReader("R-1").Debt);
    }

    [Theory]
    [InlineData("INV-1", 300.0)]
    [InlineData("INV-2", 60.0)]
    [InlineData("INV-3", 2700.0)]
    [InlineData("INV-4", 10000.0)]
    public void MarkLost_Book_ChargesCapturedReplacementCost(string inv, double expectedDebt)
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", inv, 0);

        new ReturnDesk(store).MarkLost(loan.Id);

        Assert.Equal((decimal)expectedDebt, store.FindReader("R-1").Debt);
        Assert.Equal(LoanStatus.Lost, loan.Status);
        Assert.True(store.FindReader("R-1").Blocked);
    }

    [Fact]
    public void MarkLost_UnknownBook_Charges200()
    {
        var store = NewStore();
        var loan = new Loan
        {
            Id = LibraryStore.NextLoanId++,
            ReaderCard = "R-1",
            InventoryNo = "NOPE",
            IssuedOn = DateTime.Today,
            DueDate = DateTime.Today
        };
        store.Loans.Add(loan);

        new ReturnDesk(store).MarkLost(loan.Id);

        Assert.Equal(200m, store.FindReader("R-1").Debt);
    }

    [Fact]
    public void MarkLost_Twice_ChargesOnce()
    {
        var store = NewStore();
        var loan = AddLoan(store, "R-1", "INV-1", 0);
        var desk = new ReturnDesk(store);

        desk.MarkLost(loan.Id);
        desk.MarkLost(loan.Id);

        Assert.Equal(300m, store.FindReader("R-1").Debt);
    }
}

// ---------- Звіт (ReportBuilder) ----------
public class ReportBuilderTests : TestBase
{
    [Fact]
    public void ReaderSummary_UnknownReader_ReturnsNotFoundText()
    {
        var rep = new ReportBuilder(NewStore());

        Assert.Equal("Reader R-7777 not found", rep.ReaderSummary("R-7777"));
    }

    [Fact]
    public void ReaderSummary_OverdueLoan_ShowsCapturedFine()
    {
        var store = NewStore();
        AddLoan(store, "R-1", "INV-5", 10);
        var text = new ReportBuilder(store).ReaderSummary("R-1");

        Assert.StartsWith("Reader R-1 Adult Reader (adult)", text);
        Assert.EndsWith("debt 0.00, fine 17.50", text);
    }
}

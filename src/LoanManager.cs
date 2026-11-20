namespace LibraryDesk;

// Видача та продовження книг.
// Пеню рахуємо тут же, без ReturnDesk.
public class LoanManager
{
    private const int GraceDays = 3;
    private const decimal FinePerDay = 2.5m;
    private const decimal FineCap = 150m;
    private const decimal DebtLimit = 50m;
    private const int StandardTermDays = 14;
    private const int StudentTermDays = 30;
    private const int ShortTermDays = 7;
    private const int ClosingHour = 19;
    private const int MaxExtendDays = 30;
    private const int DefaultExtendDays = 14;

    // не більше 2 продовжень (правила, п. 4.3)
    private const int MaxExtensions = 2;

    private LibraryStore store;
    public string LastError = "";
    public decimal LastFine;
    public int LastTerm;
    public int IssuedToday;
    public int DeniedToday;
    public string DeskName = "DESK-1";
    // заповнюються в Issue
    private Reader curReader;
    private Book curBook;

    public LoanManager() : this(new LibraryStore())
    {
    }

    public LoanManager(LibraryStore store)
    {
        this.store = store;
    }

    // Видача книги читачу.
    // Повертає номер видачі або -1 (див. LastError).
    // readingRoom - видача в читальну залу
    public int Issue(string cardNo, string invNo, int days, bool readingRoom)
    {
        LastError = "";
        LastFine = 0;
        // рахуємо відмову наперед, при видачі віднімаємо
        DeniedToday++;
        if (string.IsNullOrWhiteSpace(cardNo))
            return Deny("Card number is empty");
        curReader = store.FindReader(cardNo.Trim().ToUpper());
        if (curReader == null)
            return Deny("Reader not found: " + cardNo);
        curBook = store.FindBook(invNo);
        if (curBook == null || !curBook.Available)
            return Deny("Book is not available: " + invNo);

        // пеня по всіх неповернених книгах читача
        decimal fine = TotalFine(curReader);
        int onHand = CountOnHand(curReader);
        LastFine = fine;
        decimal totalDebt = fine + curReader.Debt;
        if (totalDebt > DebtLimit)
        {
            LastError = "Debt limit exceeded: " + totalDebt.ToString("0.00");
            AuditLog.Write(DeskName + " DENY " + curReader.CardNo + " debt "
                + totalDebt);
            return -1;
        }
        if (onHand >= LoanLimit(curReader))
            return Deny("Loan limit reached: " + onHand + " of " + LoanLimit(curReader));

        int term = BaseTerm(curReader, curBook);
        if (IsReadingRoomOnly(curBook))
        {
            if (!readingRoom)
                return Deny("Book " + curBook.InventoryNo + " is for the reading room only");
            if (Now().Hour >= ClosingHour)
                return Deny("Reading room closes at 20:00, no issues after 19:00");
            term = 0;
        }
        else if (fine > 0)
        {
            // боржникам - тиждень, поки не сплатять
            term = ShortTermDays;
        }
        term = RequestedTerm(term, days);
        return RegisterLoan(term);
    }

    private int CountOnHand(Reader r)
    {
        int count = 0;
        foreach (Loan l in store.Loans)
        {
            if (l.ReaderCard == r.CardNo && l.Status != LoanStatus.Returned)
                count++;
        }
        return count;
    }

    private decimal TotalFine(Reader r)
    {
        decimal fine = 0;
        foreach (Loan l in store.Loans)
        {
            if (l.ReaderCard == r.CardNo && l.Status != LoanStatus.Returned)
                fine = fine + OverdueFine(l);
        }
        return fine;
    }

    private static int BaseTerm(Reader r, Book b)
    {
        int term = StandardTermDays;
        if (r.Category == "student")
            term = StudentTermDays;
        if (b.Kind == "periodical")
            term = ShortTermDays;
        return term;
    }

    private static int RequestedTerm(int term, int days)
    {
        if (days > 0 && days < term)
            return days;
        return term;
    }

    private static bool IsReadingRoomOnly(Book b)
    {
        return b.Kind == "reference" || b.Rare;
    }

    private int Deny(string message)
    {
        LastError = message;
        return -1;
    }

    private int RegisterLoan(int term)
    {
        Loan loan = new Loan { Id = LibraryStore.NextLoanId++,
            ReaderCard = curReader.CardNo, InventoryNo = curBook.InventoryNo,
            IssuedOn = DateTime.Today, DueDate = DateTime.Today.AddDays(term) };
        store.Loans.Add(loan);
        curBook.Available = false;
        LastTerm = term;
        IssuedToday++;
        DeniedToday--;
        AuditLog.Write(DeskName + " ISSUE #" + loan.Id + " " + loan.ReaderCard + " "
            + loan.InventoryNo + " " + term + "d");
        return loan.Id;
    }

    private static decimal OverdueFine(Loan l)
    {
        if (DaysOverdue(l) <= GraceDays)
            return 0m;
        decimal pen = (DaysOverdue(l) - GraceDays) * FinePerDay;
        if (pen > FineCap)
            pen = FineCap;
        return pen;
    }

    private static int DaysOverdue(Loan l)
    {
        return (DateTime.Today - l.DueDate.Date).Days;
    }

    // Продовження видачі (за замовчуванням на 14 днів)
    public bool Extend(int loanId, int days)
    {
        Loan loan = store.FindLoan(loanId);
        if (loan == null)
        {
            LastError = "Loan not found: " + loanId;
            return false;
        }
        if (loan.Status == LoanStatus.Returned || loan.Status == LoanStatus.Lost)
        {
            LastError = "Loan #" + loanId + " is closed (" + loan.Status + ")";
            return false;
        }
        if (loan.Extensions > MaxExtensions)
        {
            LastError = "Extension limit reached for loan #" + loanId;
            return false;
        }
        if (IsOverdue(loan))
        {
            LastError = "Loan #" + loanId + " is overdue, pay the fine first";
            return false;
        }
        Book b = store.FindBook(loan.InventoryNo);
        if (b != null && (b.Kind == "periodical" || b.Rare))
        {
            LastError = "Item " + loan.InventoryNo + " cannot be extended";
            return false;
        }
        // TODO: check the reservation queue first
        if (days <= 0 || days > MaxExtendDays)
            days = DefaultExtendDays;
        loan.DueDate = loan.DueDate.AddDays(days);
        loan.Extensions++;
        loan.Status = LoanStatus.Extended;
        AuditLog.Write(DeskName + " EXTEND #" + loanId + " +" + days);
        return true;
    }

    // шов: час можна підмінити в тестах
    protected virtual DateTime Now()
    {
        return DateTime.Now;
    }
    
    // прострочена, якщо зараз пізніше дати повернення
    public bool IsOverdue(Loan loan)
    {
        if (loan.Status == LoanStatus.Returned)
            return false;
        return Now() > loan.DueDate;
    }

    // max books on hand, by reader category
    public int LoanLimit(Reader r)
    {
        // тимчасово: заблокованим 0, Issue сам відмовить
        if (r.Blocked)
            return 0;
        switch (r.Category)
        {
            case "staff":
                return 10;
            case "child":
                return 3;
            default:
                return 5;
        }
    }
}
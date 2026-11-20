namespace LibraryDesk;

// Правило пені за прострочення: єдине місце
public static class FineCalculator
{
    private const int GraceDays = 3;
    private const decimal FinePerDay = 2.5m;
    private const decimal FineCap = 150m;

    public static decimal ForLoan(Loan loan)
    {
        System.Diagnostics.Debug.Assert(loan != null, "loan is required");
        if (DaysOverdue(loan) <= GraceDays)
            return 0m;
        decimal pen = (DaysOverdue(loan) - GraceDays) * FinePerDay;
        if (pen > FineCap)
            pen = FineCap;
        return pen;
    }

    private static int DaysOverdue(Loan loan)
    {
        return (DateTime.Today - loan.DueDate.Date).Days;
    }
}
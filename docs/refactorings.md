# Журнал виконаного рефакторингу

## Таблиця виконаних кроків рефакторингу

| № | Код | Рефакторинг | Файл | Хеш | Тестів зелених |
|---:|:---:|:---|:---|:---:|:---:|
| 1 | R04 | Заміна магічних чисел на символьні константи (`GraceDays`, `FinePerDay`, `FineCap`, `DebtLimit`, терміни видачі та продовження) | `LoanManager.cs` | `83c04f4` | 18/18 |
| 2 | R03 | Спрощення обчислення загального боргу (`totalDebt = fine + curReader.Debt`) | `LoanManager.cs` | `1eac9cf` | 18/18 |
| 3 | R01, R15 | Виділення методу `OverdueFine` та усунення тимчасової змінної (`DaysOverdue`) | `LoanManager.cs` | `67893e8` | 18/18 |
| 4 | R16, R05, R01 | Декомпозиція довгого методу `Issue` (`CountOnHand`, `TotalFine`, `BaseTerm`, `RequestedTerm`, `IsReadingRoomOnly`, `RegisterLoan`, `Deny`) | `LoanManager.cs` | `42b59b3` | 18/18 |
| 5 | R09, R10, R18, R08 | Виділення `FineCalculator`, перенесення розрахунку пені (усунення «дробовика»), додавання `Debug.Assert` та заміна `switch` на словник `LimitByCategory` у `LoanLimit` | `FineCalculator.cs`, `LoanManager.cs`, `ReturnDesk.cs`, `ReportBuilder.cs` | `8b569d4` | 18/18 |

*(Примітка: якщо ти додатково закомітиш зміни для `R13` у `Models.cs` та спрощення `Extend` у `LoanManager.cs`, додай ще один рядок у таблицю з новим хешем після `git commit`.)*

---

## Приклади трансформації коду («Було / Стало»)

### 1. Декомпозиція довгого методу `LoanManager.Issue` (D-01, D-04: R01, R03, R04, R05, R16)
Початковий метод `Issue` поєднував валідацію вхідних даних, цикл підрахунку книг та пені, перевірку лімітів, обчислення терміну й реєстрацію видачі (62 логічні рядки, $CC = 21$, глибина вкладеності 5). Після виділення допоміжних методів довжина скоротилася до ~30 логічних рядків, $CC$ знизилася до **11** (на 48 %), а вкладеність зменшилася до 2 рівнів.

**Було (`LoanManager.cs`):**
```csharp
        // пеня по всіх неповернених книгах читача
        decimal fine = 0;
        int onHand = 0;
        foreach (Loan l in store.Loans)
        {
            if (l.ReaderCard == curReader.CardNo && l.Status != LoanStatus.Returned)
            {
                onHand++;
                int overdue = (DateTime.Today - l.DueDate.Date).Days;
                if (overdue > 3)
                {
                    decimal pen = (overdue - 3) * 2.5m;
                    if (pen > 150)
                        pen = 150;
                    fine = fine + pen;
                }
            }
        }
        LastFine = fine;
        if (fine + curReader.Debt > 50)
        {
            LastError = "Debt limit exceeded: " + (fine + curReader.Debt).ToString("0.00");
            AuditLog.Write(DeskName + " DENY " + curReader.CardNo + " debt "
                + (fine + curReader.Debt));
            return -1;
        }
        if (onHand >= LoanLimit(curReader))
        {
            LastError = "Loan limit reached: " + onHand + " of " + LoanLimit(curReader);
            return -1;
        }

        // термін видачі, днів
        int term = 14;
        if (curReader.Category == "student")
            term = 30;
        if (curBook.Kind == "periodical")
            term = 7;
        // якщо книга довідкова або рідкісна,
        // то тільки в зал і повернути в той же день
        if (curBook.Kind == "reference" || curBook.Rare)
        {
            if (!readingRoom)
            {
                LastError = "Book " + curBook.InventoryNo + " is for the reading room only";
                return -1;
            }
            if (DateTime.Now.Hour >= 19)
            {
                LastError = "Reading room closes at 20:00, no issues after 19:00";
                return -1;
            }
            term = 0;
        }
        else if (fine > 0)
        {
            // боржникам - тиждень, поки не сплатять
            term = 7;
        }
        if (days > 0 && days < term)
            term = days;
```

**Стало (`LoanManager.cs`):**
```csharp
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
```

---

### 2. Усунення дублювання та «дробовика» в `ReturnDesk.Return` (D-02, D-03, D-04, D-10: R09, R10, R17)
Алгоритм обчислення пені з магічними числами (`3`, `2.5m`, `150`) та мертвим закоментованим блоком замінено на єдиний виклик статичного методу `FineCalculator.ForLoan(loan)`.

**Було (`ReturnDesk.cs`):**
```csharp
        decimal charged = 0;
        // за втрачену вже виставили рахунок
        if (loan.Status != LoanStatus.Lost)
        {
            int daysLate = (DateTime.Today - loan.DueDate.Date).Days;
            if (daysLate > 3)
            {
                decimal fineAmt = (daysLate - 3) * 2.5m;
                if (fineAmt > 150)
                    fineAmt = 150;
                charged = charged + fineAmt;
            }
        }
        //int days = (DateTime.Today - loan.IssuedOn).Days - 30;
        //if (days > 0)
        //    charged = days * 2;
```

**Стало (`ReturnDesk.cs`):**
```csharp
        decimal charged = 0;
        // за втрачену вже виставили рахунок
        if (loan.Status != LoanStatus.Lost)
            charged = charged + FineCalculator.ForLoan(loan);
```

---

### 3. Заміна умовного оператора на табличний пошук у `LoanManager.LoanLimit` (D-04: R08)
Оператор `switch` з жорстко зашитими лімітами (`10`, `3`, `5`) замінено на словник `LimitByCategory` та константу `DefaultLoanLimit` зі збереженням безпечної перевірки `r.Category != null`.

**Було (`LoanManager.cs`):**
```csharp
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
```

**Стало (`LoanManager.cs`):**
```csharp
    private const int DefaultLoanLimit = 5;

    private static readonly Dictionary<string, int> LimitByCategory =
        new Dictionary<string, int>
        {
            { "staff", 10 },
            { "child", 3 }
        };

    // max books on hand, by reader category
    public int LoanLimit(Reader r)
    {
        // тимчасово: заблокованим 0, Issue сам відмовить
        if (r.Blocked)
            return 0;
        if (r.Category != null && LimitByCategory.TryGetValue(r.Category, out int limit))
            return limit;
        return DefaultLoanLimit;
    }
```

---

### 4. Централізація розрахунку пені у звіті `ReportBuilder.ReaderSummary` (D-02, D-03: R10)
Третє входження дубльованої формули пені у звіті завідувачки абонементу замінено на виклик `FineCalculator.ForLoan(l)`. Після цього глобальний пошук літералу `2.5m` по всьому рішенню дає рівно 1 збіг (у `FineCalculator.cs`).

**Було (`ReportBuilder.cs`):**
```csharp
            if (l.Status == LoanStatus.Returned)
                continue;
            int d = (DateTime.Today - l.DueDate.Date).Days;
            if (d > 3)
            {
                decimal p = (d - 3) * 2.5m;
                if (p > 150)
                    p = 150;
                fine = fine + p;
            }
        }
        sb.Append("  debt " + r.Debt.ToString("0.00") + ", fine " + fine.ToString("0.00"));
```

**Стало (`ReportBuilder.cs`):**
```csharp
            if (l.Status == LoanStatus.Returned)
                continue;
            fine = fine + FineCalculator.ForLoan(l);
        }
        sb.Append("  debt " + r.Debt.ToString("0.00") + ", fine " + fine.ToString("0.00"));
```
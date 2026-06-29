---
name: test-generation
description: Generate and run unit/integration tests TDD-style across Python, Node, and .NET. Use when adding tests to untested code, implementing a feature test-first, or finding coverage gaps.
requires_bins: [uv, python]
---

## When to use
- Adding tests to existing untested code
- TDD: write the failing test before the implementation
- Finding coverage gaps in a module before a release
- Generating parametrized edge-case tests from a spec or bug report

---

## Red-Green-Refactor loop

```
1. RED   — write a failing test that captures the desired behavior
2. GREEN — write the minimum implementation to make it pass
3. REFACTOR — clean up; tests must still pass
```
Never skip RED. A test that was never failing proves nothing.

---

## Python — pytest

### Setup
```bash
uv pip install pytest pytest-cov
```

### Basic test structure (Arrange-Act-Assert)
```python
# tests/test_orders.py
import pytest
from myapp.orders import calculate_total

def test_calculate_total_applies_discount():
    # Arrange
    items = [{"price": 100, "qty": 2}, {"price": 50, "qty": 1}]
    # Act
    result = calculate_total(items, discount=0.1)
    # Assert
    assert result == 225.0

def test_calculate_total_empty_cart_returns_zero():
    assert calculate_total([], discount=0) == 0.0

def test_calculate_total_raises_on_negative_price():
    with pytest.raises(ValueError, match="negative"):
        calculate_total([{"price": -10, "qty": 1}], discount=0)
```

### Parametrized tests
```python
@pytest.mark.parametrize("discount,expected", [
    (0.0, 100.0),
    (0.1, 90.0),
    (1.0, 0.0),
])
def test_discount_variants(discount, expected):
    assert calculate_total([{"price": 100, "qty": 1}], discount=discount) == expected
```

### Mocking boundaries
```python
from unittest.mock import patch, MagicMock

def test_send_email_called_on_order():
    with patch("myapp.orders.send_email") as mock_send:
        place_order(user_id=1, items=[...])
        mock_send.assert_called_once()
        assert "confirmation" in mock_send.call_args[0][0].lower()
```

### Run tests
```bash
pytest                          # all tests
pytest tests/test_orders.py     # single file
pytest -k "discount"            # filter by name
pytest -x                       # stop on first failure
pytest -v                       # verbose output
```

### Coverage
```bash
pytest --cov=myapp --cov-report=term-missing

# HTML report
pytest --cov=myapp --cov-report=html
# open htmlcov/index.html
```
Focus on lines shown as **not covered** in the `term-missing` column — those are the gaps.

---

## Node — Jest / Vitest

### Setup
```bash
# Jest
npm install --save-dev jest
# Vitest (Vite projects)
npm install --save-dev vitest
```

### Basic test
```js
// orders.test.js
import { calculateTotal } from './orders.js'

describe('calculateTotal', () => {
  test('applies discount correctly', () => {
    const items = [{ price: 100, qty: 2 }]
    expect(calculateTotal(items, 0.1)).toBe(180)
  })

  test('returns 0 for empty cart', () => {
    expect(calculateTotal([], 0)).toBe(0)
  })

  test('throws on negative price', () => {
    expect(() => calculateTotal([{ price: -1, qty: 1 }], 0)).toThrow()
  })
})
```

### Mocking
```js
import { sendEmail } from './email.js'
jest.mock('./email.js')

test('sends email after order', () => {
  placeOrder({ userId: 1 })
  expect(sendEmail).toHaveBeenCalledTimes(1)
})
```

### Run
```bash
npx jest              # all tests
npx jest --watch      # watch mode
npx jest --coverage   # with coverage report
npx vitest run        # vitest one-shot
```

---

## .NET — xUnit / dotnet test

### Create a test project
```bash
dotnet new xunit -n MyApp.Tests
dotnet add MyApp.Tests/MyApp.Tests.csproj reference MyApp/MyApp.csproj
```

### Basic test (Arrange-Act-Assert)
```csharp
public class OrderServiceTests
{
    [Fact]
    public void CalculateTotal_AppliesDiscount()
    {
        // Arrange
        var items = new[] { new Item(Price: 100, Qty: 2) };
        // Act
        var result = OrderService.CalculateTotal(items, discount: 0.1m);
        // Assert
        Assert.Equal(180m, result);
    }

    [Theory]
    [InlineData(0.0, 200.0)]
    [InlineData(0.1, 180.0)]
    [InlineData(1.0, 0.0)]
    public void CalculateTotal_DiscountVariants(double discount, double expected)
    {
        var items = new[] { new Item(Price: 100, Qty: 2) };
        Assert.Equal((decimal)expected,
            OrderService.CalculateTotal(items, (decimal)discount));
    }
}
```

### Run
```bash
dotnet test                          # all tests
dotnet test --filter "CalculateTotal"
dotnet test --logger "console;verbosity=normal"
```

---

## What to test vs. what to skip

| Test this | Skip this |
|-----------|-----------|
| Business logic / edge cases | Trivial getters/setters |
| Error paths and invalid input | Framework internals |
| Boundary conditions (0, null, max) | One-line wrappers with no logic |
| Integration of two non-trivial units | Auto-generated code |
| Any bug that was reported (regression) | Pure configuration |

**Mocking rule**: mock at the boundary of your system (external HTTP, DB, filesystem, time). Don't mock internal collaborators — that makes tests brittle.

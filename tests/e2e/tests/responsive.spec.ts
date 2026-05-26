import { test, expect } from '@playwright/test';
import { ADMIN_STATE } from './helpers';

test.use({ storageState: ADMIN_STATE, viewport: { width: 390, height: 800 } });

test.describe('Responsive layout (390px)', () => {
  test('the page does not overflow horizontally', async ({ page }) => {
    await page.goto('/');
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(1);
  });

  test('the wallet chip drops its "credits" label to keep the header tools visible', async ({
    page,
  }) => {
    await page.goto('/');
    await expect(page.locator('.wallet-chip')).toBeVisible();
    await expect(page.locator('.wallet-chip small')).toBeHidden();
  });

  test('a wide admin table scrolls inside its panel rather than the page', async ({ page }) => {
    await page.goto('/admin/users');
    const overflow = await page.evaluate(
      () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
    );
    expect(overflow).toBeLessThanOrEqual(1);
  });
});

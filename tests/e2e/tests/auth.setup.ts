import { test as setup } from '@playwright/test';
import { ADMIN_STATE, expectSignedIn, login } from './helpers';

setup('authenticate as admin', async ({ page }) => {
  await login(page);
  await expectSignedIn(page);
  await page.context().storageState({ path: ADMIN_STATE });
});

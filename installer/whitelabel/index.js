import fs from 'fs';
import path from 'path';
import { loadConfig, startConfigWatcher } from './loadConfig.js';

// Initial load and start watcher
loadConfig();
startConfigWatcher();

const CertificateKeyPath = path.join(process.env.DATA_PATH, 'cert', 'server.key');
const CertificatePath = path.join(process.env.DATA_PATH, 'cert', 'server.cer');
const IsHttps = fs.existsSync(CertificateKeyPath) && fs.existsSync(CertificatePath);

// Branding info
const Branding = (() => {
  const { APP_TITLE, APP_HEADER, THEME_PRIMARY_COLOR, THEME_SECONDARY_COLOR } = {
    // Default values
    ...{
      APP_TITLE: 'AV Manager',
      APP_HEADER: 'AV Manager',
      THEME_PRIMARY_COLOR: '#0055afff',
      THEME_SECONDARY_COLOR: '#f2f2f2',
    },
    // Overwritten by ENV variables
    ...process.env,
  };
  return {
    Title: APP_TITLE,
    Header: APP_HEADER,
    PrimaryColor: THEME_PRIMARY_COLOR,
    SecondaryColor: THEME_SECONDARY_COLOR,
  };
})();

const config = { CertificateKeyPath, CertificatePath, IsHttps, Branding };

export default config;

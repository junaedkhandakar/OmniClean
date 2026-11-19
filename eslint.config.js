const js = require('@eslint/js');
const globals = require('globals');

module.exports = [
    { ignores: ['node_modules/**', '.scratch/**'] },
    js.configs.recommended,
    {
        files: ['server.js', 'eslint.config.js', 'test/**/*.js', 'tools/**/*.js', 'lib/**/*.js'],
        languageOptions: {
            ecmaVersion: 2022,
            sourceType: 'commonjs',
            globals: { ...globals.node }
        }
    },
    {
        files: ['public/**/*.js', 'site/**/*.js'],
        languageOptions: {
            ecmaVersion: 2022,
            sourceType: 'script',
            globals: { ...globals.browser }
        }
    }
];

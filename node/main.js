const translate = require('google-translate-api');
const minimist = require('minimist');

const args = minimist(process.argv.slice(2));
var srcLang = args.srclang;
var targetLang = args.targetlang;
var text = args.text;

translate(text, {from: srcLang, to: targetLang}).then(res => {
	console.log(res.text);
}).catch(err => {
	console.error(err);
});
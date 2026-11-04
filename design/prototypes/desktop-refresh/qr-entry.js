import QRCode from '../../node_modules/qrcode/lib/browser.js';
window.iorQR=async value=>{if(!value)throw new Error('Empty link');return QRCode.toString(value,{type:'svg',margin:4,width:360,errorCorrectionLevel:'M',color:{dark:'#000000',light:'#ffffff'}})};
